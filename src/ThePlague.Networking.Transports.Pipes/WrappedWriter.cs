using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO;

using System.IO.Pipelines;

namespace ThePlague.Networking.Transports.Pipes
{
    internal sealed class WrappedWriter : PipeWriter
    {
        private readonly PipeWriter _writer;
        private readonly NamedPipeConnectionContext _connection;

        public WrappedWriter
        (
            PipeWriter writer,
            NamedPipeConnectionContext connection
        )
        {
            this._writer = writer;
            this._connection = connection;
        }

        public override bool CanGetUnflushedBytes
            => this._writer.CanGetUnflushedBytes;

        public override long UnflushedBytes
            => this._writer.UnflushedBytes;

        public override void Complete(Exception exception = null)
        {
            this._writer.Complete(exception);

            try
            {
                this._connection._OutputStream.Dispose();
            }
            catch
            { }
        }

        public override async ValueTask CompleteAsync(Exception exception = null)
        {
            try
            {
                //could dispose stream
                //does a flush
                await this._writer.CompleteAsync(exception);
            }
            catch
            { }

            try
            {
                await this._connection._OutputStream.DisposeAsync();
            }
            catch
            { }
        }

        public override void Advance(int bytes)
            => this._writer.Advance(bytes);

        public override void CancelPendingFlush()
            => this._writer.CancelPendingFlush();

        public override async ValueTask<FlushResult> FlushAsync
        (
            CancellationToken cancellationToken = default
        )
        { 
            try
            {
                return await this._writer.FlushAsync(cancellationToken);
            }
            catch(IOException ex)
            {
                this._connection.CompleteInput(ex);
                throw;
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
            => this._writer.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0)
            => this._writer.GetSpan(sizeHint);

        public override ValueTask<FlushResult> WriteAsync
        (
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
            => this._writer.WriteAsync(source, cancellationToken);

        // note - consider deprecated: https://github.com/dotnet/corefx/issues/38362
        [Obsolete]
        public override void OnReaderCompleted
        (
            Action<Exception, object> callback,
            object state
        )
            => this._writer.OnReaderCompleted(callback, state);
    }
}
