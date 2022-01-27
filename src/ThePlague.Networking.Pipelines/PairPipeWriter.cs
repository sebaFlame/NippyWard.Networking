using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Pipelines
{
    internal class PairPipeWriter : PipeWriter
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        private readonly PipeWriter _innerWriter;
        private readonly BasePairPipeWriter _basePipe;
        private readonly Pipe _pipe;

        internal PairPipeWriter
        (
            BasePairPipeWriter basePipe,
            PipeWriter innerWriter,
            PipeOptions pipeOptions
        )
        {
            this._basePipe = basePipe;
            this._innerWriter = innerWriter;

            this._pipe = new Pipe(pipeOptions);
            this._reader = this._pipe.Reader;
            this._writer = this._pipe.Writer;
        }

        public override void Advance(int bytes)
            => this._writer.Advance(bytes);

        public override void CancelPendingFlush()
        {
            this._innerWriter.CancelPendingFlush();
            this._writer.CancelPendingFlush();
        }

        public override void Complete(Exception exception = null)
        {
            this._innerWriter.Complete(exception);
            this._writer.Complete(exception);
        }

        public override async ValueTask<FlushResult> FlushAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            ValueTask<FlushResult> flushResultTask, innerFlushResultTask;
            ReadResult readResult;
            ValueTask<ValueTuple<SequencePosition, SequencePosition>> processTask;
            SequencePosition consumed, examined;
            FlushResult flushResult;

            flushResultTask = this._writer.FlushAsync(cancellationToken);
            if(flushResultTask.IsCompletedSuccessfully)
            {
                flushResult = flushResultTask.Result;
            }
            else
            {
                flushResult = await flushResultTask.ConfigureAwait(false);
            }

            if(flushResult.IsCompleted
                 || flushResult.IsCanceled)
            {
                return flushResult;
            }

            while(this._reader.TryRead(out readResult))
            {
                processTask = this._basePipe.ProcessWriteAsync
                (
                    in readResult,
                    this._innerWriter
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

                this._reader.AdvanceTo(consumed, examined);
            }

            innerFlushResultTask
                = this._innerWriter.FlushAsync(cancellationToken);

            if(!flushResultTask.IsCompletedSuccessfully)
            {
                await innerFlushResultTask.ConfigureAwait(false);
            }

            return flushResult;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
            => this._writer.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0)
            => this._writer.GetSpan(sizeHint);
    }
}