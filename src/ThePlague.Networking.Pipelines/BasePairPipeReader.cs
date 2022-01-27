using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace ThePlague.Networking.Pipelines
{
    public abstract class BasePairPipeReader : BasePassThroughPipeReader
    {
        public override PipeReader Input => this._enabledPipe.Input;
        public override PipeWriter Output => this._enabledPipe.Output;

        private IDuplexPipe _enabledPipe;
        private readonly IDuplexPipe _passthroughPipe;
        private readonly IDuplexPipe _pairedPipe;

        protected BasePairPipeReader
        (
            IDuplexPipe parentPipe,
            PipeOptions pipeOptions
        )
            : base(parentPipe)
        {
            this._passthroughPipe = new DuplexPipe
            (
                this.PassThroughReader,
                parentPipe.Output
            );

            this._pairedPipe = new DuplexPipe
            (
                new PairPipeReader
                (
                    this,
                    parentPipe.Input,
                    pipeOptions
                ),
                parentPipe.Output
            );
        }

        public void EnablePassThrough()
            => Interlocked.Exchange
            (
                ref this._enabledPipe,
                this._passthroughPipe
            );

        public void DisablePassThrough()
            => Interlocked.Exchange
            (
                ref this._enabledPipe,
                this._pairedPipe
            );

        public abstract ValueTask<ValueTuple<SequencePosition, SequencePosition>> ProcessReadAsync
        (
            in ReadResult readResult,
            PipeWriter writer
        );
    }
}