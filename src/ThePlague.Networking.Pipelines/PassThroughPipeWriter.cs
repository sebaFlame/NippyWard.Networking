using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Pipelines
{
    internal class PassThroughPipeWriter : PipeWriter
    {
        private readonly PipeWriter _innerWriter;
        private readonly PipeReader _innerReader;
        private readonly BasePassThroughPipeWriter _basePipe;

        internal PassThroughPipeWriter
        (
            BasePassThroughPipeWriter basePipe,
            IDuplexPipe innerPipe
        )
        {
            this._basePipe = basePipe;
            this._innerWriter = innerPipe.Output;
            this._innerReader = innerPipe.Input;
        }

        public override bool CanGetUnflushedBytes
            => this._innerWriter.CanGetUnflushedBytes;

        public override long UnflushedBytes
            => this._innerWriter.UnflushedBytes;

        public override void Advance(int bytes)
            => this._innerWriter.Advance(bytes);

        public override void CancelPendingFlush()
            => this._innerWriter.CancelPendingFlush();

        public override void Complete(Exception exception = null)
            => this._innerWriter.Complete(exception);

        public override async ValueTask<FlushResult> FlushAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            ValueTask<FlushResult> flushResultTask;
            ReadResult readResult;
            FlushResult flushResult;
            ValueTask<ReadResult> readResultTask;

            //first queue a read
            readResultTask = this._innerReader.ReadAsync(cancellationToken);
            if(readResultTask.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException
                (
                    "Read/Writes out of order!"
                );
            }

            //then flush the current writes
            flushResultTask = this._innerWriter.FlushAsync(cancellationToken);
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

            //should always be synchronous!
            if(readResultTask.IsCompletedSuccessfully)
            {
                readResult = readResultTask.Result;
            }
            else
            {
                readResult = await readResultTask.ConfigureAwait(false);
            }

            this._basePipe.ProcessWrite(in readResult);

            return flushResult;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
            => this._innerWriter.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0)
            => this._innerWriter.GetSpan(sizeHint);
    }
}