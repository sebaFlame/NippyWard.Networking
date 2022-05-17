using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using OpenSSL.Core.SSL;
using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.X509;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Tls
{
    internal partial class TlsPipe : ITlsConnectionFeature, ITlsHandshakeFeature, IDuplexPipe, IDisposable
    {
        #region IDuplexPipe
        public PipeReader Input => this.TlsPipeReader;
        public PipeWriter Output => this.TlsPipeWriter;
        #endregion

        #region ITlsConnectionFeature
        public X509Certificate Certificate => this._ssl.Certificate;
        public X509Certificate RemoteCertificate => this._ssl.RemoteCertificate;
        public SslSession Session => this._ssl.Session;
        #endregion

        #region ITlsHandshakeFeature
        public string Cipher => this._ssl.Cipher;
        public SslProtocol Protocol => this._ssl.Protocol;
        #endregion

        internal readonly TlsPipeReader TlsPipeReader;
        internal readonly TlsPipeWriter TlsPipeWriter;

        private readonly string _connectionId;
        private readonly ILogger _logger;
        private readonly PipeReader _innerReader;
        private readonly PipeWriter _innerWriter;

        private static Task _WriteCompletedTask;
        private Task _writeAwaiter;
        private TaskCompletionSource<bool> _renegotiateWaiter;

        private Ssl _ssl;

        private readonly TlsBuffer _decryptedReadBuffer;
        private readonly TlsBuffer _unencryptedWriteBuffer;

        static TlsPipe()
        {
            _WriteCompletedTask = Task.CompletedTask;
        }

        public TlsPipe        
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            ILogger logger,
            MemoryPool<byte> pool = null
        )
        {
            this._connectionId = connectionId;

            this._decryptedReadBuffer = new TlsBuffer(pool);
            this._unencryptedWriteBuffer = new TlsBuffer(pool);

            this._innerReader = innerReader;
            this._innerWriter = innerWriter;

            this.TlsPipeReader = new TlsPipeReader(this);
            this.TlsPipeWriter = new TlsPipeWriter(this);

            this._logger = logger;
            this._writeAwaiter = null;
        }

        #region authentication
        private async Task Authenticate
        (
            Ssl ssl,
            PipeReader pipeReader,
            TlsBuffer decryptedReadBuffer,
            PipeWriter pipeWriter,
            CancellationToken cancellationToken
        )
        {
            SslState sslState = default;
            ReadResult readResult;
            FlushResult flushResult;
            ReadOnlySequence<byte> buffer;
            SequencePosition read;

            this.TraceLog($"authenticating TLS as {(ssl.IsServer ? "server" : "client")}");

            while (sslState.WantsRead()
                || sslState.WantsWrite()
                || !ssl.DoHandshake(out sslState))
            {
                if (sslState.WantsWrite())
                {
                    //get a buffer from the ssl object
                    sslState = ssl.WriteSsl
                    (
                        ReadOnlySpan<byte>.Empty,
                        pipeWriter,
                        out _
                    );

                    cancellationToken.ThrowIfCancellationRequested();

                    //flush to the other side
                    flushResult = await pipeWriter.FlushAsync(cancellationToken);

                    if (flushResult.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (flushResult.IsCompleted)
                    {
                        //if handshake not completed, throw exception
                        if (!ssl.DoHandshake(out sslState))
                        {
                            ThrowPipeCompleted();
                        }

                        this.TraceLog($"pipe writer completed during handshake with state {sslState}");

                        //user data might have been received, leave further processing to consumer
                        break;
                    }
                }
                    
                if (sslState.WantsRead())
                {
                    //get a buffer from the other side
                    readResult = await pipeReader.ReadAsync(cancellationToken);

                    buffer = readResult.Buffer;

                    //read the received data into the ssl object
                    //and read possible application data into decryptedReadBuffer
                    sslState = ssl.ReadSsl
                    (
                        buffer,
                        decryptedReadBuffer,
                        out read
                    );

                    pipeReader.AdvanceTo(read);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (readResult.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (readResult.IsCompleted)
                    {
                        //if handshake not completed, throw exception
                        if (!ssl.DoHandshake(out sslState))
                        {
                            ThrowPipeCompleted();
                        }

                        this.TraceLog($"pipe reader completed during handshake with state {sslState}");

                        //user data might have been received, leave further processing to consumer
                        break;
                    }
                }
            }

            this.TraceLog("authenticated TLS");
        }
        #endregion

        #region reading
        internal void AdvanceTo(SequencePosition consumed)
            => this._decryptedReadBuffer.AdvanceReader(consumed);

        internal void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            => this._decryptedReadBuffer.AdvanceReader(consumed, examined);

        internal void CancelPendingRead()
            => this._innerReader.CancelPendingRead();

        internal void CompleteReader(Exception exception = null)
            => this._innerReader.Complete(exception);

        internal ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (this._decryptedReadBuffer.Length > 0)
            {
                this.TraceLog("buffered read available");

                //get the decrypted buffer
                CreateReadResultFromTlsBuffer
                (
                    this._decryptedReadBuffer,
                    out ReadResult tlsResult
                );

                return new ValueTask<ReadResult>(tlsResult);
            }

            ReadResult readResult;
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

        private ValueTask<ReadResult> ReturnReadResult(bool bufferRead, CancellationToken cancellationToken)
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
                out ReadResult tlsResult
            );

            return new ValueTask<ReadResult>(tlsResult);
        }

        private ValueTask<ReadResult> ProcessReadResult(in ReadResult readResult, CancellationToken cancellationToken)
        {
            ReadResult tlsResult;
            ReadOnlySequence<byte> buffer = readResult.Buffer;
            SequencePosition readPosition;

            SslState sslState = this.ProcessReadResult
            (
                in buffer,
                out readPosition
            );

            if (!(readResult.IsCompleted
                || readResult.IsCanceled))
            {
                if (sslState.WantsWrite())
                {
                    this.TraceLog("write during read requested");
                    return this.WriteDuringReadAsync<ReadResult>
                    (
                        this.ReturnReadResult,
                        //retry read if buffer not read completely
                        buffer.IsEmpty || buffer.End.Equals(readPosition),
                        cancellationToken
                    );
                }

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

        private void ProcessRenegotiate(in SslState sslState)
        {
            if (sslState.HandshakeCompleted())
            {
                this.TraceLog("handshake completed (authenticate/renegotiate)");

                this._renegotiateWaiter?.SetResult(true);
                this._renegotiateWaiter = null;
            }
        }

        private void ProcessRenegotiate(Exception ex)
        {
            this._renegotiateWaiter?.SetException(ex);
            this._renegotiateWaiter = null;
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

                this.TraceLog($"ReadSsl (buffer {buffer.Length})");

                if (!buffer.IsEmpty
                    && !buffer.Start.Equals(readPosition))
                {
                    this._innerReader.AdvanceTo(readPosition);
                }

                if (sslState.IsShutdown())
                {
                    this.TraceLog("shutdown during read requested");

                    ThrowTlsShutdown();
                }

                this.ProcessRenegotiate(in sslState);
            }
            catch(Exception ex)
            {
                this.ProcessRenegotiate(ex);
                throw;
            }

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
            out ReadResult tlsResult
        )
        {
            decryptedReadBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> result);
            tlsResult = new ReadResult(result, false, false);
        }

        internal bool TryRead(out ReadResult readResult)
        {
            if(this._decryptedReadBuffer.Length == 0)
            {
                readResult = default;
                return false;
            }

            CreateReadResultFromTlsBuffer(this._decryptedReadBuffer, out readResult);
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

        private ValueTask<T> WriteDuringReadAsync<T>
        (
            Func<bool, CancellationToken, ValueTask<T>> postWriteFunction,
            bool bufferRead,
            CancellationToken cancellationToken
        )
        {
            ValueTask<FlushResult> flushTask;
            FlushResult flushResult;

            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            //do a zero length write
            try
            {
                flushTask = this.FlushAsyncCore
                (
                    in ReadOnlySequence<byte>.Empty,
                    tcs.Task,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }

            if (!flushTask.IsCompletedSuccessfully)
            {
                return this.AwaitWriteDuringReadAsync
                (
                    flushTask,
                    tcs,
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
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                tcs.SetResult();
            }

            return postWriteFunction(bufferRead, cancellationToken);
        }

        private async ValueTask<T> AwaitWriteDuringReadAsync<T>
        (
            ValueTask<FlushResult> flushTask,
            TaskCompletionSource tcs,
            bool bufferRead,
            Func<bool ,CancellationToken, ValueTask<T>> postWriteFunction,
            CancellationToken cancellationToken
        )
        {
            try
            {
                FlushResult flushResult = await flushTask.ConfigureAwait(false);

                this.TraceLog("async flush");

                //as these can come from read/renegotiate, throw an exception to bubble to caller
                if (flushResult.IsCompleted)
                {
                    ThrowPipeCompleted();
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                tcs.SetResult();
            }

            return await postWriteFunction(bufferRead, cancellationToken);
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
                in flushedBuffer,
                _WriteCompletedTask,
                cancellationToken
            );
        }

        private ValueTask<FlushResult> FlushAsyncCore
        (
            in ReadOnlySequence<byte> buffer,
            Task replaceTask,
            CancellationToken cancellationToken
        )
        {
            Task writeAwaitable;
            ValueTask<FlushResult> flushTask;
            SequencePosition readPosition;
#if TRACE
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
#if TRACE
                    count++;
#endif
                }

                this.TraceLog($"buffer {buffer.Length} spun for {count} cycles");

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
                            in buffer,
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
            in ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken,
            out SequencePosition readPosition,
            out ValueTask<FlushResult> flushTask
        )
        {
            SslState sslState;
            PipeWriter pipeWriter = this._innerWriter;

            try
            {
                sslState = this._ssl.WriteSsl
                (
                    buffer,
                    pipeWriter,
                    out readPosition
                );

                this.TraceLog($"WriteSsl (buffer {buffer.Length})");

                //only advance reader when a read has occured
                if (!buffer.IsEmpty
                    && !buffer.Start.Equals(readPosition))
                {
                    //advance write reader
                    this._unencryptedWriteBuffer.AdvanceReader(readPosition);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (sslState.IsShutdown())
                {
                    this.TraceLog("shutdown during write requested");

                    ThrowTlsShutdown();
                }

                this.ProcessRenegotiate(in sslState);

                if (sslState.WantsWrite())
                {
                    throw new InvalidOperationException("write requested during write");
                }

                if (sslState.WantsRead())
                {
                    this.TraceLog("read during write requested");
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

                this.TraceLog("initiated flush");
                flushTask = pipeWriter.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                //ensure current renegotiation finishes
                this.ProcessRenegotiate(ex);

                //rethrow
                throw;
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
        /// <summary>
        /// Initialize and complete a SSL/TLS renegotatiate.
        /// Ensure you're always reading.
        /// </summary>
        public ValueTask<bool> RenegotiateAsync(CancellationToken cancellationToken = default)
        {
            SslState sslState;
            this._renegotiateWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask<bool> renegotiateTask = new ValueTask<bool>(this._renegotiateWaiter.Task);

            this.TraceLog("renegotiating");

            try
            {
                sslState = this._ssl.DoRenegotiate();
            }
            catch (Exception)
            {
                this._renegotiateWaiter = null;
                throw;
            }

            //can happen during TLS1.3 "renegotiate"
            this.ProcessRenegotiate(sslState);

            if (sslState.WantsWrite())
            {
                ValueTask<bool> ReturnRenegotiateTask(bool bufferRead, CancellationToken cancellationToken)
                    => renegotiateTask;

                this.TraceLog("write during renegotiate requested");
                return this.WriteDuringReadAsync(ReturnRenegotiateTask, true, cancellationToken);
            }

            if (sslState.WantsRead())
            {
                this.TraceLog("read during renegotiate requested");
            }

            return new ValueTask<bool>(this._renegotiateWaiter.Task);
        }
        #endregion

        private static void ThrowPipeCompleted()
            => throw new InvalidOperationException("Pipe has been completed");

        private static void ThrowTlsShutdown()
            => throw new TlsShutdownException();

        [Conditional("TRACE")]
        private void TraceLog(string message, [CallerFilePath] string file = null, [CallerMemberName] string caller = null, [CallerLineNumber] int lineNumber = 0)
        {
#if TRACE
            this._logger?.TraceLog(this._connectionId, message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");
#endif
        }

        public void Dispose()
        {
            this._ssl.Dispose();

            Interlocked.Exchange(ref this._writeAwaiter, null);
        }
    }
}
