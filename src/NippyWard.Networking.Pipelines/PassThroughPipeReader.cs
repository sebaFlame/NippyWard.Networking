using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace NippyWard.Networking.Pipelines
{
    internal class PassThroughPipeReader : PipeReader
    {
        private readonly PipeReader _reader;
        private readonly BasePassThroughPipeReader _basePipe;

        public PassThroughPipeReader
        (
            BasePassThroughPipeReader basePipe,
            PipeReader innerReader
        )
        {
            this._basePipe = basePipe;
            this._reader = innerReader;
        }

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

        public override void Complete(Exception exception = null)
            => this._reader.Complete(exception);

        public override async ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            ReadResult readResult;
            ValueTask<ReadResult> readResultTask
                = this._reader.ReadAsync(cancellationToken);

            if(readResultTask.IsCompletedSuccessfully)
            {
                readResult = readResultTask.Result;
            }
            else
            {
                readResult = await readResultTask.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            this._basePipe.ProcessRead
            (
                in readResult
            );

            return readResult;
        }

        public override bool TryRead(out ReadResult result)
        {
            if(this._reader.TryRead(out result))
            {
                this._basePipe.ProcessRead(in result);
                return true;
            }

            return false;
        }
    }
}
