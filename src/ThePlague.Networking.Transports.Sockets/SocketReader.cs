using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Pipelines;

using ThePlague.Networking.Transports;

namespace ThePlague.Networking.Transports.Sockets
{
    internal class SocketReader : WrappedReader
    {
        private readonly SocketConnectionContext _connection;

        public SocketReader
        (
            PipeReader reader,
            SocketConnectionContext connection
        )
            : base(reader)
        {
            this._connection = connection;
        }

        public override void Complete(Exception exception = null)
        {
            this._connection.InputReaderCompleted(exception);
            this._reader.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception exception = null)
        {
            try
            {
                this._connection.InputReaderCompleted(exception);
                await this._reader.CompleteAsync(exception);
            }
            catch
            { }

            //await the send thread
            //use a task, so it can be awaited on multiple times
            await this._connection.AwaitReceiveTask();
        }
    }
}
