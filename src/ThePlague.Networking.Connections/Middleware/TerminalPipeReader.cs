using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Connections.Middleware
{
    internal class TerminalPipeReader : PipeReader
    {
        private readonly IDuplexPipe _pipe;
        private readonly TaskCompletionSource _terminalCompleted;

        public TerminalPipeReader
        (
            IDuplexPipe pipe,
            TaskCompletionSource terminalCompleted
        )
        {
            this._pipe = pipe;
            this._terminalCompleted = terminalCompleted;
        }

        public override void AdvanceTo(SequencePosition consumed)
            => this._pipe.Input.AdvanceTo(consumed);

        public override void AdvanceTo
        (
            SequencePosition consumed,
            SequencePosition examined
        )
            => this._pipe.Input.AdvanceTo(consumed, examined);

        public override void CancelPendingRead()
            => this._pipe.Input.CancelPendingRead();

        public override void Complete(Exception exception = null)
        {
            this._pipe.Input.Complete(exception);

            if(exception is not null)
            {
                this._terminalCompleted.TrySetException(exception);
            }
            else
            {
                this._terminalCompleted.TrySetResult();
            }
        }

        public override ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
            => this._pipe.Input.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
            => this._pipe.Input.TryRead(out result);

    }
}
