using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace ThePlague.Networking.Connections
{
    internal sealed class TerminalPipeWriter : PipeWriter
    {
        private readonly IDuplexPipe _pipe;
        private readonly ConnectionTerminal _terminal;

        public TerminalPipeWriter
        (
            IDuplexPipe pipe,
            ConnectionTerminal terminal
        )
        {
            this._pipe = pipe;
            this._terminal = terminal;
        }

        public override void Complete(Exception exception = null)
        {
            this._pipe.Output.Complete(exception);

            if(exception is not null)
            {
                this._terminal.ConnectionCompleted.TrySetException(exception);
            }
            else
            {
                this._terminal.ConnectionCompleted.TrySetResult(null);
            }
        }

        public override void Advance(int bytes)
            => this._pipe.Output.Advance(bytes);

        public override void CancelPendingFlush()
            => this._pipe.Output.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync
        (
            CancellationToken cancellationToken = default
        )
            => this._pipe.Output.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
            => this._pipe.Output.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0)
            => this._pipe.Output.GetSpan(sizeHint);

        public override ValueTask<FlushResult> WriteAsync
        (
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
            => this._pipe.Output.WriteAsync(source, cancellationToken);

        // note - consider deprecated: https://github.com/dotnet/corefx/issues/38362
        [Obsolete]
        public override void OnReaderCompleted
        (
            Action<Exception, object> callback,
            object state
        )
            => this._pipe.Output.OnReaderCompleted(callback, state);
    }
}
