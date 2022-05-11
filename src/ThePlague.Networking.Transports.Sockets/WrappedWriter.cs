using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace ThePlague.Networking.Transports.Sockets
{
    internal sealed class WrappedWriter : PipeWriter
    {
        private readonly PipeWriter _writer;
        private readonly SocketConnectionContext _connection;

        public WrappedWriter(PipeWriter writer, SocketConnectionContext connection)
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
            this._connection.OutputWriterCompleted(exception);
            this._writer.Complete(exception);
        }

        public override void Advance(int bytes)
            => this._writer.Advance(bytes);

        public override void CancelPendingFlush()
            => this._writer.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync
        (
            CancellationToken cancellationToken = default
        )
            => this._writer.FlushAsync(cancellationToken);

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
