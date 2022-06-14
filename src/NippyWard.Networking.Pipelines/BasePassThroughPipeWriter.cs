using System.IO.Pipelines;

namespace NippyWard.Networking.Pipelines
{
    public abstract class BasePassThroughPipeWriter : IDuplexPipe
    {
        public IDuplexPipe WrappedPipe => this._pipe;
        public virtual PipeReader Input => this._pipe.Input;
        public virtual PipeWriter Output => this.PassThroughWriter;

        protected PipeWriter PassThroughWriter { get; private set; }

        private readonly IDuplexPipe _pipe;

        protected BasePassThroughPipeWriter
        (
            IDuplexPipe pipe
        )
        {
            this._pipe = pipe;

            this.PassThroughWriter = new PassThroughPipeWriter
            (
                this,
                pipe
            );
        }

        public abstract void ProcessWrite
        (
            in ReadResult readResult
        );
    }
}