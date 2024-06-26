﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Pipelines;

using NippyWard.Networking.Transports;

namespace NippyWard.Networking.Transports.Sockets
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

        public override void Complete(Exception? exception = null)
        {
            this._connection.InputReaderCompleted(exception);
            base.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            this._connection.InputReaderCompleted(exception);
            await this._reader.CompleteAsync(exception);

            try
            {
                //await the receive thread
                //use a task, so it can be awaited on multiple times
                await this._connection.AwaitReceiveTask();
            }
            catch
            { }
        }
    }
}
