using System.IO.Pipelines;

namespace ThePlague.Networking.Pipelines
{
    public abstract class BasePassThroughPipeReader : IDuplexPipe
    {
        public IDuplexPipe WrappedPipe => this._pipe;
        public virtual PipeReader Input => this.PassThroughReader;
        public virtual PipeWriter Output => this._pipe.Output;

        protected PipeReader PassThroughReader { get; private set; }

        private readonly IDuplexPipe _pipe;

        protected BasePassThroughPipeReader
        (
            IDuplexPipe pipe
        )
        {
            this._pipe = pipe;

            this.PassThroughReader = new PassThroughPipeReader
            (
                this,
                pipe.Input
            );
        }

        public abstract void ProcessRead
        (
            in ReadResult readResult
        );
    }
}