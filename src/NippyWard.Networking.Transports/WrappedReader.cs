using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace NippyWard.Networking.Transports
{
    public abstract class WrappedReader : PipeReader
    {
        protected readonly PipeReader _reader;

        public WrappedReader
        (
            PipeReader reader
        )
        {
            this._reader = reader;
        }

        public override void Complete(Exception? exception = null)
            => this._reader.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null)
            => this._reader.CompleteAsync(exception);

        public override void AdvanceTo(SequencePosition consumed)
            => this._reader.AdvanceTo(consumed);

        public override void AdvanceTo
        (
            SequencePosition consumed,
            SequencePosition examined
        )
            => this._reader.AdvanceTo(consumed, examined);

        public override void CancelPendingRead()
            => this._reader.CancelPendingRead();

        public override ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default
        )
            => this._reader.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
            => this._reader.TryRead(out result);

        // note - consider deprecated: https://github.com/dotnet/corefx/issues/38362
        [Obsolete]
        public override void OnWriterCompleted
        (
            Action<Exception?, object?> callback,
            object? state
        )
            => this._reader.OnWriterCompleted(callback, state);
    }
}
