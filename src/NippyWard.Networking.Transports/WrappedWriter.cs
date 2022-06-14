using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace NippyWard.Networking.Transports
{
    public abstract class WrappedWriter : PipeWriter
    {
        protected readonly PipeWriter _writer;

        public WrappedWriter
        (
            PipeWriter writer
        )
        {
            this._writer = writer;
        }

        public override bool CanGetUnflushedBytes
            => this._writer.CanGetUnflushedBytes;

        public override long UnflushedBytes
            => this._writer.UnflushedBytes;

        public override void Complete(Exception? exception = null)
            => this._writer.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null)
            => this._writer.CompleteAsync(exception);

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
            Action<Exception?, object?> callback,
            object? state
        )
            => this._writer.OnReaderCompleted(callback, state);
    }
}
