using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Connections
{
    internal class TerminalPipeReader : PipeReader
    {
        private readonly IDuplexPipe _pipe;
        private readonly ConnectionTerminal _terminal;

        public TerminalPipeReader
        (
            IDuplexPipe pipe,
            ConnectionTerminal terminal
        )
        {
            this._pipe = pipe;
            this._terminal = terminal;
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
                this._terminal.ConnectionCompleted.TrySetException(exception);
            }
            else
            {
                this._terminal.ConnectionCompleted.TrySetResult(null);
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
