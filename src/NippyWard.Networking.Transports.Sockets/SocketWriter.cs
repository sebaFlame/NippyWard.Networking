using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

using System.IO.Pipelines;

using NippyWard.Networking.Transports;

namespace NippyWard.Networking.Transports.Sockets
{
    internal class SocketWriter : WrappedWriter
    {
        private readonly SocketConnectionContext _connection;

        public SocketWriter
        (
            PipeWriter writer,
            SocketConnectionContext connection
        )
            : base(writer)
        {
            this._connection = connection;
        }

        public override void Complete(Exception? exception = null)
        {
            this._connection.OutputWriterCompleted(exception);
            base.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            this._connection.OutputWriterCompleted(exception);
            await this._writer.CompleteAsync(exception);

            try
            {
                //await the send thread
                //use a task, so it can be awaited on multiple times
                await this._connection.AwaitSendTask();
            }
            catch
            { }
        }
    }
}
