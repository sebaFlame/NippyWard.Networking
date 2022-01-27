using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Pipelines
{
    internal class PairPipeReader : PipeReader
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        private readonly PipeReader _innerReader;
        private readonly BasePairPipeReader _basePipe;
        private readonly Pipe _pipe;

        internal PairPipeReader
        (
            BasePairPipeReader basePipe,
            PipeReader innerReader,
            PipeOptions pipeOptions
        )
        {
            this._basePipe = basePipe;
            this._innerReader = innerReader;

            this._pipe = new Pipe(pipeOptions);
            this._reader = this._pipe.Reader;
            this._writer = this._pipe.Writer;
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
        {
            this._innerReader.Complete(exception);
            this._reader.Complete(exception);
        }

        public override async ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            ReadResult readResult;
            ValueTask<ValueTuple<SequencePosition, SequencePosition>> processTask;
            ValueTask<ReadResult> readResultTask;
            SequencePosition consumed, examined;

            while(true)
            {
                readResultTask = this._innerReader.ReadAsync(cancellationToken);

                if(readResultTask.IsCompletedSuccessfully)
                {
                    readResult = readResultTask.Result;
                }
                else
                {
                    readResult = await readResultTask.ConfigureAwait(false);
                }

                do
                {
                    processTask = this._basePipe.ProcessReadAsync
                    (
                        in readResult,
                        this._writer
                    );

                    if(processTask.IsCompletedSuccessfully)
                    {
                        (consumed, examined) = processTask.Result;
                    }
                    else
                    {
                        (consumed, examined) =
                            await processTask.ConfigureAwait(false);
                    }

                    this._innerReader.AdvanceTo(consumed, examined);

                } while(this._innerReader.TryRead(out readResult));

                if(this._reader.TryRead(out ReadResult processedResult))
                {
                    return processedResult;
                }
            }
        }

        public override bool TryRead(out ReadResult result)
            => this._reader.TryRead(out result);
    }
}