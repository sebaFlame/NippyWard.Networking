using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace ThePlague.Networking.Transports.Sockets
{
    internal sealed class WrappedReader : PipeReader
    {
        private readonly PipeReader _reader;
        private readonly SocketConnectionContext _connection;

        public WrappedReader
        (
            PipeReader reader,
            SocketConnectionContext connection
        )
        {
            this._reader = reader;
            this._connection = connection;
        }

        public override void Complete(Exception exception = null)
        {
            this._connection.InputReaderCompleted(exception);
            this._reader.Complete(exception);
        }

        public override ValueTask CompleteAsync(Exception exception = null)
        {
            try
            {
                this.Complete(exception);
            }
            catch
            { }

            if (this._connection._receiveTask is null)
            {
                return default;
            }

            //await the send thread
            //use a task, so it can be awaited on multiple times
            return new ValueTask(this._connection._receiveTask);
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

        public override ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default
        )
            => this._reader.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
            => this._reader.TryRead(out result);

        // note - consider deprecated: https://github.com/dotnet/corefx/issues/38362
        [Obsolete]
        public override void OnWriterCompleted
        (
            Action<Exception, object> callback,
            object state
        )
            => this._reader.OnWriterCompleted(callback, state);
    }
}
