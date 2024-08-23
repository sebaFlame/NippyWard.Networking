using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using NippyWard.OpenSSL.SSL;
using NippyWard.OpenSSL.SSL.Buffer;
using NippyWard.OpenSSL.X509;

using NippyWard.Networking.Logging;
using System.Diagnostics.CodeAnalysis;
using NippyWard.OpenSSL.Error;
using System.IO;

namespace NippyWard.Networking.Tls
{
    public partial class TlsPipe : ITlsConnectionFeature, ITlsHandshakeFeature, IDuplexPipe, IDisposable, IAsyncDisposable
    {
        #region IDuplexPipe
        public PipeReader Input => this.TlsPipeReader;
        public PipeWriter Output => this.TlsPipeWriter;
        #endregion

        #region ITlsConnectionFeature
        public X509Certificate? Certificate => this._ssl.Certificate;
        public X509Certificate? RemoteCertificate => this._ssl.RemoteCertificate;
        public SslSession? Session => this._ssl.Session;
        #endregion

        #region ITlsHandshakeFeature
        public string? Cipher => this._ssl.Cipher;
        public SslProtocol? Protocol => this._ssl.Protocol;
        #endregion

        internal readonly TlsPipeReader TlsPipeReader;
        internal readonly TlsPipeWriter TlsPipeWriter;

        private readonly string _connectionId;
        private readonly ILogger? _logger;
        private readonly PipeReader _innerReader;
        private readonly PipeWriter _innerWriter;
        private readonly Ssl _ssl;
        private readonly TlsBuffer _decryptedReadBuffer;
        private readonly TlsBuffer _unencryptedWriteBuffer;

        private Task? _writeAwaiter;

        private static Task _WriteCompletedTask;

        static TlsPipe()
        {
            _WriteCompletedTask = Task.CompletedTask;
        }

        private TlsPipe
        (
            Ssl ssl,
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            TlsBuffer decryptedReadBuffer,
            TlsBuffer unencryptedWriteBuffer,
            ILogger? logger = null
        )
        {
            this._ssl = ssl;
            this._connectionId = connectionId;

            this._decryptedReadBuffer = decryptedReadBuffer;
            this._unencryptedWriteBuffer = unencryptedWriteBuffer;

            this._innerReader = innerReader;
            this._innerWriter = innerWriter;

            this.TlsPipeReader = new TlsPipeReader(this);
            this.TlsPipeWriter = new TlsPipeWriter(this);

            this._logger = logger;
            this._writeAwaiter = null;
        }

        #region authentication
        //TODO: when PipeScheduler.ThreadPool, resumeWriterThreshold and pauseWriterThreshold are enabled
        //how to guarantee a send?
        private static async Task Authenticate
        (
            Ssl ssl,
            string connectionId,
            PipeReader pipeReader,
            TlsBuffer decryptedReadBuffer,
            PipeWriter pipeWriter,
            ILogger? logger,
            CancellationToken cancellationToken
        )
        {
            SslState sslState = default;
            bool completed;

            logger?.TraceLog(connectionId, $"authenticating TLS as {(ssl.IsServer ? "server" : "client")}");

            while (!ssl.DoHandshake(out sslState))
            {
                logger?.TraceLog(connectionId, $"authenticating state of {sslState} for {(ssl.IsServer ? "server" : "client")}");

                (sslState, completed) = await ReadWriteAsync
                (
                    ssl,
                    connectionId,
                    pipeReader,
                    decryptedReadBuffer,
                    pipeWriter,
                    logger,
                    sslState,
                    cancellationToken
                );


                //if pipe was completed (connection EOF)
                if (completed)
                {
                    //and handshake was completed
                    //data might be available to read
                    //allow TlsPipe to be created
                    if (ssl.DoHandshake(out sslState))
                    {
                        logger?.TraceLog(connectionId, "completed during authentication with successful handshake");

                        break;
                    }
                    //handshake not completed, no data should be available
                    //throw an exception
                    else
                    {
                        logger?.TraceLog(connectionId, "completed during authentication without successful handshake");

                        ThrowPipeCompleted();
                    }
                }

                if(sslState.IsShutdown())
                {
                    logger?.TraceLog(connectionId, "shutdown during authentication");

                    //finish the shutdown to prevent a deadlock (both server and client waiting on a read)
                    await ShutdownAsyncCore
                    (
                        ssl,
                        connectionId,
                        pipeReader,
                        decryptedReadBuffer,
                        pipeWriter,
                        logger,
                        cancellationToken
                    );

                    //still allow to create a TlsPipe after shutdown succeeds
                    //data might be available to read
                    break;
                }
            }

            logger?.TraceLog(connectionId, "authenticated TLS");
        }
        #endregion

        #region readwrite
        private static async Task<(SslState, bool)> ReadWriteAsync
        (
            Ssl ssl,
            string connectionId,
            PipeReader pipeReader,
            TlsBuffer decryptedReadBuffer,
            PipeWriter pipeWriter,
            ILogger? logger,
            SslState sslState,
            CancellationToken cancellationToken,
            [CallerMemberName] string? method = null
        )
        {
            ValueTask<ReadResult> readTask;
            ValueTask<FlushResult> flushTask;
            ReadResult readResult;
            FlushResult flushResult;
            ReadOnlySequence<byte> buffer;
            SequencePosition read;
            bool completed = false;

            if (sslState.WantsWrite())
            {
                //get a buffer from the ssl object
                sslState = ssl.WriteSsl
                (
                    ReadOnlySequence<byte>.Empty,
                    pipeWriter,
                    out _
                );

                logger?.TraceLog(connectionId, $"{method}: write of {pipeWriter.UnflushedBytes} for {(ssl.IsServer ? "server" : "client")} with state {sslState}");

                if (pipeWriter.UnflushedBytes == 0)
                {
                    return (sslState, completed);
                }

                //flush to the other side
                flushTask = pipeWriter.FlushAsync(cancellationToken);

                if (flushTask.IsCompletedSuccessfully)
                {
                    flushResult = flushTask.Result;
                }
                else
                {
                    flushResult = await flushTask;
                }

                if (flushResult.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else if (flushResult.IsCompleted)
                {
                    logger?.TraceLog(connectionId, $"pipe writer completed during {method} with state {sslState}");

                    completed = true;
                }
            }
            else if (sslState.WantsRead())
            {
                //get a buffer from the other side
                readTask = pipeReader.ReadAsync(cancellationToken);

                if (readTask.IsCompletedSuccessfully)
                {
                    readResult = readTask.Result;
                }
                else
                {
                    readResult = await readTask;
                }

                buffer = readResult.Buffer;
                read = buffer.Start;

                try
                {
                    sslState = ssl.ReadSsl
                    (
                        buffer,
                        decryptedReadBuffer,
                        out read
                    );

                    logger?.TraceLog(connectionId, $"{method}: read of {buffer.Length} for {(ssl.IsServer ? "server" : "client")} with state {sslState}");
                }
                finally
                {
                    pipeReader.AdvanceTo(read);
                }

                if (readResult.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else if (readResult.IsCompleted)
                {
                    logger?.TraceLog(connectionId, $"pipe reader completed during {method} with state {sslState}");

                    //user data might have been received, leave further processing to consumer
                    //eg call TryRead
                    completed = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            return (sslState, completed);
        }
        #endregion

        #region reading
        internal void AdvanceTo(SequencePosition consumed)
            => this._decryptedReadBuffer.AdvanceReader(consumed);

        internal void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            => this._decryptedReadBuffer.AdvanceReader(consumed, examined);

        internal void CancelPendingRead()
            => this._innerReader.CancelPendingRead();

        internal void CompleteReader(Exception? exception = null)
            => this._innerReader.Complete(exception);

        internal ValueTask CompleteReaderAsync(Exception? exception = null)
            => this._innerReader.CompleteAsync(exception);

        internal ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult readResult;
            //first prioritize a sync read
            if (this._innerReader.TryRead(out readResult))
            {
                return this.ProcessReadResult(in readResult, cancellationToken);
            }

            //then check the exisiting buffer
            if (this._decryptedReadBuffer.Length > 0)
            {
                this.TraceLog("buffered read available");

                //get the decrypted buffer
                CreateReadResultFromTlsBuffer
                (
                    this._decryptedReadBuffer,
                    cancellationToken.IsCancellationRequested,
                    false,
                    out ReadResult tlsResult
                );

                return new ValueTask<ReadResult>(tlsResult);
            }

            //then try an async read
            ValueTask<ReadResult> readResultTask = this._innerReader.ReadAsync(cancellationToken);
            if (!readResultTask.IsCompletedSuccessfully)
            {
                return this.AwaitInnerReadAsync(readResultTask, cancellationToken);
            }

            readResult = readResultTask.Result;

            return this.ProcessReadResult(in readResult, cancellationToken);
        }

        private async ValueTask<ReadResult> AwaitInnerReadAsync(ValueTask<ReadResult> readResultTask, CancellationToken cancellationToken)
        {
            ReadResult readResult = await readResultTask.ConfigureAwait(false);

            this.TraceLog("async read");

            return await this.ProcessReadResult(in readResult, cancellationToken);
        }

        private ValueTask<ReadResult> ReturnReadResult(FlushResult flushResult, bool bufferRead, CancellationToken cancellationToken)
        {
            if (!bufferRead
                || this._decryptedReadBuffer.Length == 0)
            {
                return this.ReadAsync(cancellationToken);
            }

            //get the decrypted buffer
            CreateReadResultFromTlsBuffer
            (
                this._decryptedReadBuffer,
                cancellationToken.IsCancellationRequested,
                false,
                out ReadResult tlsResult
            );

            return new ValueTask<ReadResult>(tlsResult);
        }

        private ValueTask<ReadResult> ProcessReadResult(in ReadResult readResult, CancellationToken cancellationToken)
        {
            ReadResult tlsResult;
            ReadOnlySequence<byte> buffer = readResult.Buffer;
            SequencePosition readPosition = buffer.Start;
            SslState sslState;

            try
            {
                sslState = this.ProcessReadResult
                (
                    in buffer,
                    out readPosition
                );
            }
            finally
            {
                //always advance, to remove reading state
                this._innerReader.AdvanceTo(readPosition);
            }

            if(!readResult.IsCompleted)
            {
                //finish shutdown
                if (sslState.IsShutdown())
                {
                    this.TraceLog("shutdown during read requested");
                    return this.ShutdownDuringReadAsync
                    (
                        buffer.IsEmpty || buffer.End.Equals(readPosition),
                        cancellationToken
                    );
                }

                if (sslState.WantsWrite())
                {
                    this.TraceLog("write during read requested");
                    return this.WriteDuringOperation<ReadResult>
                    (
                        this.ReturnReadResult,
                        //retry read if buffer not read completely
                        buffer.IsEmpty || buffer.End.Equals(readPosition),
                        cancellationToken
                    );
                }

                //TODO: stack overflow
                if (sslState.WantsRead())
                {
                    this.TraceLog("read during read requested");
                    return this.ReadAsync(cancellationToken);
                }
            }

            //get the decrypted buffer
            CreateReadResultFromTlsBuffer
            (
                this._decryptedReadBuffer,
                in readResult,
                out tlsResult
            );

            return new ValueTask<ReadResult>(tlsResult);
        }

        private SslState ProcessReadResult
        (
            in ReadOnlySequence<byte> buffer,
            out SequencePosition readPosition
        )
        {
            SslState sslState;

            try
            {
                //read the received data into the ssl object
                //and read possible application data into decryptedReadBuffer
                sslState = this._ssl.ReadSsl
                (
                    buffer,
                    this._decryptedReadBuffer,
                    out readPosition
                );
            }
            catch (OpenSslException e) when (e.Errors.Any(x => x.Library == 20 && x.Reason == 207)) //protocol is shutdown
            {
                throw new TlsShutdownException();
            }

            this.TraceLog($"ReadSsl (buffer {buffer.Length})");

            return sslState;
        }

        private static void CreateReadResultFromTlsBuffer
        (
            TlsBuffer decryptedReadBuffer,
            in ReadResult readResult,
            out ReadResult tlsResult
        )
        {
            decryptedReadBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> result);
            tlsResult = new ReadResult(result, readResult.IsCanceled, readResult.IsCompleted);
        }

        private static void CreateReadResultFromTlsBuffer
        (
            TlsBuffer decryptedReadBuffer,
            bool isCanceled,
            bool isCompleted,
            out ReadResult tlsResult
        )
        {
            decryptedReadBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> result);
            tlsResult = new ReadResult(result, isCanceled, isCompleted);
        }

        internal bool TryRead(out ReadResult readResult)
        {
            if(this._decryptedReadBuffer.Length == 0)
            {
                readResult = default;
                return false;
            }

            CreateReadResultFromTlsBuffer
            (
                this._decryptedReadBuffer,
                false,
                false,
                out readResult
            );

            return true;

            /* don't do this, can not react to state changes
            if (!this._innerReader.TryRead(out ReadResult innerResult))
            {
                readResult = default;
                return false;
            }

            if(this.ProcessReadResult(in innerResult, out readResult) == SslState.NONE)
            {
                return true;
            }
            */
        }

        private ValueTask<T> WriteDuringOperation<T>
        (
            Func<FlushResult, bool, CancellationToken, ValueTask<T>> postWriteFunction,
            bool bufferRead,
            CancellationToken cancellationToken
        )
        {
            ValueTask<FlushResult> flushTask;
            FlushResult flushResult;

            cancellationToken.ThrowIfCancellationRequested();

            //use a Task based one as there could be multiple awaiters
            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration reg = cancellationToken.Register((tcs) => ((TaskCompletionSource<int>?)tcs!).SetCanceled(), tcs);

            //do a zero length write
            try
            {
                ReadOnlySequence<byte> buffer = ReadOnlySequence<byte>.Empty;
                flushTask = this.FlushAsyncCore
                (
                    ref buffer,
                    tcs.Task,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                reg.Dispose();
                throw;
            }

            if (!flushTask.IsCompletedSuccessfully)
            {
                return this.AwaitWriteDuringOperation
                (
                    flushTask,
                    tcs,
                    reg,
                    bufferRead,
                    postWriteFunction,
                    cancellationToken
                );
            }

            try
            {
                flushResult = flushTask.Result;

                this.TraceLog("sync flush");

                //as these can come from read/renegotiate, throw an exception to bubble to caller
                if (flushResult.IsCompleted)
                {
                    ThrowPipeCompleted();
                }

                tcs.SetResult(0);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                reg.Dispose();
            }

            return postWriteFunction(flushResult, bufferRead, cancellationToken);
        }

        private async ValueTask<T> AwaitWriteDuringOperation<T>
        (
            ValueTask<FlushResult> flushTask,
            TaskCompletionSource<int> tcs,
            CancellationTokenRegistration reg,
            bool bufferRead,
            Func<FlushResult, bool ,CancellationToken, ValueTask<T>> postWriteFunction,
            CancellationToken cancellationToken
        )
        {
            FlushResult flushResult;

            try
            {
                flushResult = await flushTask.ConfigureAwait(false);

                this.TraceLog("async flush");

                //as these can come from read/renegotiate, throw an exception to bubble to caller
                if (flushResult.IsCompleted)
                {
                    ThrowPipeCompleted();
                }

                tcs.SetResult(0);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                reg.Dispose();
            }

            return await postWriteFunction(flushResult, bufferRead, cancellationToken);
        }
        #endregion

        #region writing
        internal bool CanGetUnflushedBytes
            => this._innerWriter.CanGetUnflushedBytes;

        public long UnflushedBytes
            => this._innerWriter.UnflushedBytes;

        internal void Advance(int bytes)
            => this._unencryptedWriteBuffer.Advance(bytes);

        internal void CancelPendingFlush()
            => this._innerWriter.CancelPendingFlush();

        internal void CompleteWriter(Exception? exception = null)
            => this._innerWriter.Complete(exception);

        internal ValueTask CompleteWriterAsync(Exception? exception = null)
            => this._innerWriter.CompleteAsync(exception);

        internal Memory<byte> GetMemory(int sizeHint = 0)
            => this._unencryptedWriteBuffer.GetMemory(sizeHint);

        internal Span<byte> GetSpan(int sizeHint = 0)
            => this._unencryptedWriteBuffer.GetSpan(sizeHint);

        internal ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (this._unencryptedWriteBuffer.Length == 0)
            {
                this.TraceLog("no data to be flushed");

                return default;
            }

            this._unencryptedWriteBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> flushedBuffer);
            return this.FlushAsyncCore
            (
                ref flushedBuffer,
                _WriteCompletedTask,
                cancellationToken
            );
        }

        private ValueTask<FlushResult> FlushAsyncCore
        (
            ref ReadOnlySequence<byte> buffer,
            Task replaceTask,
            CancellationToken cancellationToken
        )
        {
            Task? writeAwaitable;
            ValueTask<FlushResult> flushTask;
            SequencePosition readPosition;
#if TRACELOG
            int count = 0;
#endif

            do
            {
                //spin until thread wins race
                while ((writeAwaitable = Interlocked.CompareExchange(ref this._writeAwaiter, replaceTask, null)) != null)
                {
                    //regular flush
                    if(object.ReferenceEquals(replaceTask, _WriteCompletedTask))
                    {
                        //double flush detected
                        if(object.ReferenceEquals(writeAwaitable, _WriteCompletedTask))
                        {
                            throw new InvalidOperationException("Only 1 flush at a time allowed");
                        }
                        //awaitable found, use it
                        else
                        {
                            break;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    //this is a bad idea!
                    Thread.SpinWait(1);
#if TRACELOG
                    count++;
#endif
                }
#if TRACELOG
                this.TraceLog($"buffer {buffer.Length} spun for {count} cycles");
#endif

                cancellationToken.ThrowIfCancellationRequested();

                //a write during read/renegotiate has been initiated
                if (writeAwaitable is not null)
                {
                    this.TraceLog($"buffer {buffer.Length} awaitable received");
                    return this.AwaitTaskAndRetryFlushAsync
                    (
                        writeAwaitable,
                        cancellationToken
                    );
                }
                //happy path :)
                else
                {
                    try
                    {
                        if (this.ProcessWrite
                        (
                            ref buffer,
                            cancellationToken,
                            out readPosition,
                            out flushTask
                        ))
                        {
                            //ssl write succeeded
                            this.TraceLog($"buffer {buffer.Length} success");

                            //writer gets freed in ProcessFlushResult
                            break;
                        }
                        else
                        {
                            //ssl write did not succeed
                            this.TraceLog($"buffer {buffer.Length} failure");

                            //ensure writer is freed
                            _ = Interlocked.Exchange(ref this._writeAwaiter, null);

                            //give other threads a chance to win the race
                            this.TraceLog($"buffer {buffer.Length} sleeping");
                            Thread.Sleep(1);
                        }
                    }
                    catch(Exception ex)
                    {
                        this.TraceLog($"buffer {buffer.Length} exception during flush: {ex}");

                        //ensure writer is freed
                        _ = Interlocked.Exchange(ref this._writeAwaiter, null);

                        throw;
                    }
                }
            } while (true);

            this.TraceLog($"buffer {buffer.Length} returning");
            return this.ProcessFlushResult
            (
                flushTask,
                //retry flush if buffer not written completely
                buffer.IsEmpty || buffer.End.Equals(readPosition),
                cancellationToken
            );
        }

        //await task and retry write
        //state has already changed to writing at this stage!
        private async ValueTask<FlushResult> AwaitTaskAndRetryFlushAsync
        (
            Task awaitableTask,
            CancellationToken cancellationToken
        )
        {
            this.TraceLog($"awaiting out of band write");

            //this awaitable will free _writeAwaiter
            await awaitableTask.ConfigureAwait(false);

            this.TraceLog($"awaited out of band write");

            cancellationToken.ThrowIfCancellationRequested();

            //the awaitableTask should have changed state back to null
            //or another thread has taken it
            //retry taking _writeAwaiter
            return await this.FlushAsync
            (
                cancellationToken
            );
        }

        //TODO: refactor to not pass a ReadOnlySequence<byte>
        //this buffer can come from anywhere, while _unencryptedWriteBuffer gets advanced
        private bool ProcessWrite
        (
            ref ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken,
            out SequencePosition readPosition,
            out ValueTask<FlushResult> flushTask
        )
        {
            SslState sslState;
            PipeWriter pipeWriter = this._innerWriter;
            readPosition = buffer.Start;

            try
            {
                try
                {
                    sslState = this._ssl.WriteSsl
                    (
                        buffer,
                        pipeWriter,
                        out readPosition
                    );
                }
                catch(OpenSslException e) when (e.Errors.Any(x => x.Library == 20 && x.Reason == 207)) //protocol is shutdown
                {
                    throw new TlsShutdownException();
                }

                this.TraceLog($"WriteSsl (buffer {buffer.Length})");

                cancellationToken.ThrowIfCancellationRequested();

                if (sslState.IsShutdown())
                {
                    this.TraceLog("shutdown during write discovered");
                }

                if(sslState.WantsRead())
                {
                    this.TraceLog("read during write discovered");
                }

                //nothing has been written to PipeWriter (IBufferWriter)
                if (pipeWriter.UnflushedBytes == 0)
                {
                    //this write could come from read/renegotiate
                    //and hasn't won the race
                    //return true to prevent deadlock on read
                    if (buffer.IsEmpty)
                    {
                        this.TraceLog($"0 unflushed bytes on empty buffer, skip flush ({sslState})");
                        flushTask = default;
                        return true;
                    }
                    //else other write thread has won race and awaits a read (or other operation)
                    //when the write won the race it would've gotten non-application bytes from WriteSsl
                    //these get prioritized, and an operation (read) is awaited on
                    //the buffer gets completely ignored, until the wanted read (or other operation) gets statisfied
                    else
                    {
                        flushTask = default;

                        //might have been a partial ssl write (to guarantee 16k payload) which hasn't been flushed yet
                        if (!buffer.Start.Equals(readPosition))
                        {
                            this.TraceLog($"0 unflushed bytes on filled buffer, flush to ssl succeeded, cancel flush to writer ({sslState})");

                            //cancel flush
                            return true;
                        }

                        this.TraceLog($"0 unflushed bytes on filled buffer, retry flush ({sslState})");

                        //retry flush
                        return false;
                    }
                }

                //write requested, continue FlushAsyncCore loop and retry buffer
                //without calling pipeWriter.FlushAsync (execute synchronously)
                if (sslState.WantsWrite())
                {
                    this.TraceLog($"write requested during write ({sslState})");
                    flushTask = default;
                    return false;
                }

                this.TraceLog("initiated flush");
                flushTask = pipeWriter.FlushAsync(cancellationToken);
            }
            finally
            {
                //only advance reader when a read has occured
                if (!buffer.IsEmpty
                    && !buffer.Start.Equals(readPosition))
                {
                    //advance write reader
                    this._unencryptedWriteBuffer.AdvanceReader(readPosition);
                    buffer = buffer.Slice(readPosition);
                }
            }

            return true;
        }

        private ValueTask<FlushResult> ProcessFlushResult
        (
            ValueTask<FlushResult> flushTask,
            bool bufferFlushed,
            CancellationToken cancellationToken
        )
        {
            if (!flushTask.IsCompletedSuccessfully)
            {
                return this.AwaitInnerFlushAsync
                (
                    flushTask,
                    bufferFlushed,
                    cancellationToken
                );
            }

            try
            {
                //get result to get possible exception
                this.TraceLog("sync flush");

                //get result to throw possible exceptions
                _ = flushTask.Result;

                if (bufferFlushed)
                {
                    return flushTask;
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref this._writeAwaiter, null);
            }

            return this.FlushAsync
            (
                cancellationToken
            );
        }

        private async ValueTask<FlushResult> AwaitInnerFlushAsync
        (
            ValueTask<FlushResult> flushTask,
            bool bufferFlushed,
            CancellationToken cancellationToken
        )
        {
            try
            {
                FlushResult flushResult = await flushTask.ConfigureAwait(false);
                this.TraceLog("async flush");

                if (bufferFlushed)
                {
                    return flushResult;
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref this._writeAwaiter, null);
            }

            return await this.FlushAsync
            (
                cancellationToken
            );
        }
        #endregion

        #region renegotiate
        private class TlsPipeValueTaskAwaiter : IValueTaskSource<bool>
        {
            private ManualResetValueTaskSourceCore<bool> _core;

            public TlsPipeValueTaskAwaiter()
            {
                this._core = new ManualResetValueTaskSourceCore<bool>();
            }

            public bool GetResult(short token)
                => this._core.GetResult(token);

            public ValueTaskSourceStatus GetStatus(short token)
                => this._core.GetStatus(token);

            public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
                => this._core.OnCompleted(continuation, state, token, flags);

            public void SetResult(bool result)
                => this._core.SetResult(result);

            public void SetException(Exception exception)
                => this._core.SetException(exception);
        }

        /// <summary>
        /// Initialize and complete an SSL/TLS renegotiation.
        /// Ensure you're not reading/writing anymore.
        /// Not cancellable.
        /// </summary>
        public async Task RenegotiateAsync()
        {
            this.TraceLog("renegotiating");

            while (!this._ssl.DoRenegotiate(out SslState sslState))
            {
                this._logger?.TraceLog(this._connectionId, $"renegotiating state of {sslState} for {(this._ssl.IsServer ? "server" : "client")}");

                await ReadWriteAsync
                (
                    this._ssl,
                    this._connectionId,
                    this._innerReader,
                    this._decryptedReadBuffer,
                    this._innerWriter,
                    this._logger,
                    sslState,
                    CancellationToken.None
                );
            }

            this.TraceLog("renegotiate completed");
        }

        /// <summary>
        /// Initialize an SSL/TLS renegotiation.
        /// This method expects a read thread to be active to be able to proceed with the renegotiation.
        /// Not cancellable.
        /// </summary>
        public Task InitializeRenegotiateAsync()
        {
            this.TraceLog("initializing renegotiate");

            if(this._ssl.DoRenegotiate(out SslState sslState))
            {
                return Task.FromException(new InvalidOperationException("Renegotiation already completed"));
            }

            return ReadWriteAsync
            (
                this._ssl,
                this._connectionId,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                this._logger,
                sslState,
                CancellationToken.None
            );
        }
        #endregion

        #region Shutdown
        //complete a shutdown request
        private async ValueTask<ReadResult> ShutdownDuringReadAsync
        (
            bool bufferRead,
            CancellationToken cancellationToken
        )
        {
            SslState sslState;
            ReadResult readResult;

            while(!ShutdownInternal(this._ssl, this._connectionId, this._logger, out sslState))
            {
                if (sslState.WantsWrite())
                {
                    static ValueTask<bool> ReturnShutdownTask(FlushResult flushResult, bool bufferRead, CancellationToken cancellationToken)
                    {
                        return new ValueTask<bool>(flushResult.IsCompleted);
                    }

                    await this.WriteDuringOperation<bool>
                    (
                        ReturnShutdownTask,
                        bufferRead,
                        cancellationToken
                    );
                }
                else if(sslState.WantsRead())
                {
                    //this should only recurse 1 level deep
                    return await this.ReadAsync(cancellationToken); 
                }
            }

            CreateReadResultFromTlsBuffer
            (
                this._decryptedReadBuffer,
                cancellationToken.IsCancellationRequested,
                true,
                out readResult
            );

            return readResult;
        }

        private static bool ShutdownInternal
        (
            Ssl ssl,
            string connectionId,
            ILogger? logger,
            out SslState sslState
        )
        {
            bool succes = ssl.DoShutdown(out sslState);

            if(succes)
            {
                TraceLog(logger, connectionId, "shutdown completed");
            }

            return succes;
        }

        private static async Task ShutdownAsyncCore
        (
            Ssl ssl,
            string connectionId,
            PipeReader pipeReader,
            TlsBuffer decryptedReadBuffer,
            PipeWriter pipeWriter,
            ILogger? logger,
            CancellationToken cancellationToken
        )
        {
            TraceLog(logger, connectionId, "shutting down");

            while (!ShutdownInternal(ssl, connectionId, logger, out SslState sslState))
            {
                TraceLog(logger, connectionId, $"shutting down state of {sslState} for {(ssl.IsServer ? "server" : "client")}");

                await ReadWriteAsync
                (
                    ssl,
                    connectionId,
                    pipeReader,
                    decryptedReadBuffer,
                    pipeWriter,
                    logger,
                    sslState,
                    cancellationToken
                );
            }
        }

        /// <summary>
        /// Shut down the SSL/TLS connection.
        /// Ensure you're not reading/writing anymore.
        /// Not cancellable.
        /// </summary>
        public Task ShutdownAsync()
        {
            return ShutdownAsyncCore
            (
                this._ssl,
                this._connectionId,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                this._logger,
                CancellationToken.None
            );
        }

        /// <summary>
        /// Initializes an SSL/TLS shutdown.
        /// This method expects a read thread to be active to proceed with the shutdown.
        /// Not cancellable.
        /// </summary>
        public Task InitializeShutdownAsync()
        {
            this.TraceLog("initializing shutdown");

            if(ShutdownInternal(this._ssl, this._connectionId, this._logger, out SslState sslState))
            {
                return Task.FromException(new InvalidOperationException("Shutdown already completed"));
            }

            return ReadWriteAsync
            (
                this._ssl,
                this._connectionId,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                this._logger,
                sslState,
                CancellationToken.None
            );
        }
        #endregion

        [DoesNotReturn]
        private static void ThrowPipeCompleted()
            => throw new InvalidOperationException("Pipe has been completed");

        [Conditional("TRACELOG")]
        private static void TraceLog
        (
            ILogger? logger,
            string connectionId,
            string message,
            [CallerFilePath] string? file = null,
            [CallerMemberName] string? caller = null,
            [CallerLineNumber] int lineNumber = 0
        )
        {
#if TRACELOG
            logger?.TraceLog(connectionId, message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");
#endif
        }

        [Conditional("TRACELOG")]
        private void TraceLog
        (
            string message,
            [CallerFilePath] string? file = null,
            [CallerMemberName] string? caller = null,
            [CallerLineNumber] int lineNumber = 0
        )
        {
#if TRACELOG
            TraceLog(this._logger, this._connectionId, message, file, caller, lineNumber);
#endif
        }

        public async ValueTask DisposeAsync()
        {
            await this.ShutdownAsync();

            this.Dispose();
        }

        public void Dispose()
        {
            this._ssl?.Dispose();

            Interlocked.Exchange(ref this._writeAwaiter, null);
        }
    }
}
