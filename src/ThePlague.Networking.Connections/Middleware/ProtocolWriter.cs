using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Connections.Middleware
{
    public class ProtocolWriter<TMessage> : IProtocolWriter<TMessage>, IDisposable
    {
        private readonly IDuplexPipe _pipe;
        private readonly IMessageWriter<TMessage> _writer;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public ProtocolWriter
        (
            IDuplexPipe pipe,
            IMessageWriter<TMessage> writer
        )
        {
            this._pipe = pipe;
            this._writer = writer;
            this._semaphore = new SemaphoreSlim(1);
        }

        public void Complete(Exception ex = null)
            => this._pipe.Output.Complete(ex);

        public ValueTask CompleteAsync(Exception ex = null)
            => this._pipe.Output.CompleteAsync(ex);

        /// <summary>
        /// Writes a message to the transport using an <see cref="IMessageWriter{TMessage}"/>.
        /// This method uses a (async) semaphore to allow only 1 write at a time.
        /// </summary>
        /// <param name="message">The message to transmit</param>
        /// <param name="cancellationToken">Cancellation token to cancel the write</param>
        /// <returns>An awaitable valuetask</returns>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="OperationCanceledException"></exception>
        public async ValueTask WriteAsync
        (
            TMessage message,
            CancellationToken cancellationToken = default
        )
        {
            if(this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }

            await this._semaphore
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            PipeWriter pipeWriter = this._pipe.Output;
            IMessageWriter<TMessage> writer = this._writer;

            try
            {
                if(this._disposed)
                {
                    throw new ObjectDisposedException(this.GetType().Name);
                }

                writer.WriteMessage(message, pipeWriter);

                FlushResult result;
                ValueTask<FlushResult> resultTask = pipeWriter.FlushAsync(cancellationToken);

                if(resultTask.IsCompletedSuccessfully)
                {
                    result = resultTask.Result;
                }
                else
                {
                    result = await resultTask;
                }

                if(result.IsCanceled)
                {
                    throw new OperationCanceledException();
                }

                if(result.IsCompleted)
                {
                    this._disposed = true;
                }
            }
            finally
            {
                this._semaphore.Release();
            }
        }

        public void Dispose()
        {
            this._disposed = true;

            try
            {
                this._semaphore.Dispose();
            }
            catch
            { }
        }
    }
}
