using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Connections
{
    public class ProtocolWriter<TMessage> : IDisposable, IAsyncDisposable
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

        public void Complete(Exception ex)
            => this._pipe.Output.Complete(ex);

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
                ValueTask<FlushResult> resultTask
                    = pipeWriter.FlushAsync(cancellationToken);

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

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return default;
        }
    }
}
