using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using OpenSSL.Core.SSL;
using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.X509;

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
            PipeReader innerReader,
            PipeWriter innerWriter,
            ILogger logger,
            MemoryPool<byte> pool = null
        )
        {
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
        private static async Task Authenticate
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

                    if (flushResult.IsCompleted)
                    {
                        //if handshake not completed, throw exception
                        if(!ssl.DoHandshake(out _))
                        {
                            ThrowPipeCompleted();
                        }

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

                    pipeReader.AdvanceTo(read, buffer.End);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (readResult.IsCompleted)
                    {
                        //if handshake not completed, throw exception
                        if (!ssl.DoHandshake(out _))
                        {
                            ThrowPipeCompleted();
                        }

                        //user data might have been received, leave further processing to consumer
                        break;
                    }
                }
            }
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
                return this.ReturnReadResult(cancellationToken);
            }

            ReadResult readResult;
            ValueTask<ReadResult> readResultTask = this._innerReader.ReadAsync(cancellationToken);
            if (!readResultTask.IsCompletedSuccessfully)
            {
                return this.ReadAsyncInternal(readResultTask, cancellationToken);
            }

            readResult = readResultTask.Result;

            return this.ProcessReadResult(in readResult, cancellationToken);
        }

        private async ValueTask<ReadResult> ReadAsyncInternal(ValueTask<ReadResult> readResultTask, CancellationToken cancellationToken)
        {
            ReadResult readResult = await readResultTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return await this.ProcessReadResult(in readResult, cancellationToken);
        }

        private ValueTask<ReadResult> ReturnReadResult(CancellationToken cancellationToken)
        {
            if (this._decryptedReadBuffer.Length == 0)
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
            SslState sslState = this.ProcessReadResult(in readResult);

            if(!(readResult.IsCompleted
                || readResult.IsCanceled))
            {
                if (sslState.IsShutdown())
                {
                    ThrowTlsShutdown();
                }

                if (sslState.WantsWrite())
                {
                    return this.WriteDuringReadAsync<ReadResult>(this.ReturnReadResult, cancellationToken);
                }

                if (sslState.WantsRead())
                {
                    //should not happen
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
                this._renegotiateWaiter?.SetResult(true);
                this._renegotiateWaiter = null;
            }
        }

        private void ProcessRenegotiate(Exception ex)
        {
            this._renegotiateWaiter?.SetException(ex);
            this._renegotiateWaiter = null;
        }

        private SslState ProcessReadResult(in ReadResult readResult)
        {
            ReadResult res = readResult;
            ReadOnlySequence<byte> buffer = res.Buffer;
            SslState sslState;
            SequencePosition read;

            try
            {
                //read the received data into the ssl object
                //and read possible application data into decryptedReadBuffer
                sslState = this._ssl.ReadSsl
                (
                    buffer,
                    this._decryptedReadBuffer,
                    out read
                );
            }
            catch(Exception ex)
            {
                this.ProcessRenegotiate(ex);
                throw;
            }

            this._innerReader.AdvanceTo(read, buffer.End);

            this.ProcessRenegotiate(in sslState);

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
            Func<CancellationToken, ValueTask<T>> postWriteFunction,
            CancellationToken cancellationToken
        )
        {
            ValueTask<FlushResult> flushTask;
            FlushResult flushResult;

            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource tcs = new TaskCompletionSource();
            Task t;

            //spin untill thread wins race
            while ((t = Interlocked.CompareExchange(ref this._writeAwaiter, tcs.Task, null)) != null)
            {
                //this is a bad idea!
                Thread.SpinWait(1);
            }

            cancellationToken.ThrowIfCancellationRequested();

            //do a zero length write
            try
            {
                flushTask = this.ProcessWrite
                (
                    in ReadOnlySequence<byte>.Empty,
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
                    postWriteFunction,
                    cancellationToken
                );
            }

            try
            {
                flushResult = flushTask.Result;

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

            return postWriteFunction(cancellationToken);
        }

        private async ValueTask<T> AwaitWriteDuringReadAsync<T>
        (
            ValueTask<FlushResult> flushTask,
            TaskCompletionSource tcs,
            Func<CancellationToken, ValueTask<T>> postWriteFunction,
            CancellationToken cancellationToken
        )
        {
            try
            {
                FlushResult flushResult = await flushTask;

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

            return await postWriteFunction(cancellationToken);
        }
        #endregion

        #region writing
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
            if(this._unencryptedWriteBuffer.Length == 0)
            {
                return default;
            }

            Task writing = _WriteCompletedTask;
            Task writeAwaitable = Interlocked.Exchange(ref this._writeAwaiter, writing);

            //either already writing
            //or a write during read/renegotiate has been initiated
            if (writeAwaitable is not null)
            {
                if (object.ReferenceEquals(writeAwaitable, writing))
                {
                    throw new NotSupportedException("Only 1 flush at at time is allowed");
                }

                return this.AwaitTaskAndRetryFlushAsync(writeAwaitable, cancellationToken);
            }
            //happy path :)
            else
            {
                this._unencryptedWriteBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> flushedBuffer);
                return this.ProcessWrite(in flushedBuffer, cancellationToken);
            }   
        }

        //await task and retry write
        //state has already changed to writing at this stage!
        internal async ValueTask<FlushResult> AwaitTaskAndRetryFlushAsync(Task awaitableTask, CancellationToken cancellationToken = default)
        {
            await awaitableTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            this._unencryptedWriteBuffer.CreateReadOnlySequence(out ReadOnlySequence<byte> flushedBuffer);
            return await this.ProcessWrite(in flushedBuffer, cancellationToken);
        }

        private ValueTask<FlushResult> ProcessWrite(in ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            SslState sslState;
            SequencePosition read;
            ValueTask<FlushResult> flushTask;

            try
            {
                sslState = this._ssl.WriteSsl
                (
                    buffer,
                    this._innerWriter,
                    out read
                );

                if (!buffer.IsEmpty)
                {
                    //advance write reader
                    this._unencryptedWriteBuffer.AdvanceReader(read, buffer.End);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (sslState.WantsWrite())
                {
                    //should not happen!
                    return this.ProcessWrite(ReadOnlySequence<byte>.Empty, cancellationToken);
                }

                flushTask = this._innerWriter.FlushAsync(cancellationToken);
            }
            catch(Exception ex)
            {
                //ensure current renegotiation finishes
                this.ProcessRenegotiate(ex);

                //ensure writer is freed
                _ = Interlocked.Exchange(ref this._writeAwaiter, null);

                //rethrow
                throw;
            }

            if (!flushTask.IsCompletedSuccessfully)
            {
                return this.ProcessInnerFlushAsync(flushTask, sslState);
            }

            try
            {
                this.ProcessRenegotiate(in sslState);

                if (sslState.IsShutdown())
                {
                    ThrowTlsShutdown();
                }

                return new ValueTask<FlushResult>(flushTask.Result);
            }
            finally
            {
                _ = Interlocked.Exchange(ref this._writeAwaiter, null);
            }
        }

        private async ValueTask<FlushResult> ProcessInnerFlushAsync(ValueTask<FlushResult> flushTask, SslState sslState)
        {
            try
            {
                //prioritize flush
                FlushResult flushResult = await flushTask;

                this.ProcessRenegotiate(in sslState);

                if (sslState.IsShutdown())
                {
                    ThrowTlsShutdown();
                }

                return flushResult;
            }
            finally
            {
                _ = Interlocked.Exchange(ref this._writeAwaiter, null);
            }
        }
        #endregion

        #region renegotiate
        public ValueTask<bool> RenegotiateAsync(CancellationToken cancellationToken = default)
        {
            SslState sslState;
            this._renegotiateWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                sslState = this._ssl.DoRenegotiate();
            }
            catch (Exception)
            {
                this._renegotiateWaiter = null;
                throw;
            }

            ValueTask<bool> ReturnRenegotiateTask(CancellationToken cancellationToken)
                => new ValueTask<bool>(this._renegotiateWaiter.Task);

            //can happen during TLS1.3 "renegotiate"
            if(sslState.HandshakeCompleted())
            {
                this._renegotiateWaiter.SetResult(true);
            }

            if (sslState.WantsWrite())
            {
                return this.WriteDuringReadAsync(ReturnRenegotiateTask, cancellationToken);
            }
            else if (sslState.WantsRead())
            {
                //always reading
            }

            return new ValueTask<bool>(this._renegotiateWaiter.Task);
        }
        #endregion

        private static void ThrowPipeCompleted()
            => throw new InvalidOperationException("Pipe has been completed");

        private static void ThrowTlsShutdown()
            => throw new TlsShutdownException();

        public void Dispose()
        {
            this._ssl.Dispose();
        }
    }
}
