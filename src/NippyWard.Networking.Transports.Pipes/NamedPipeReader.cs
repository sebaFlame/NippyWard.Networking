using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO;

using System.IO.Pipelines;

using NippyWard.Networking.Transports;

namespace NippyWard.Networking.Transports.Pipes
{
    internal sealed class NamedPipeReader : WrappedReader
    {
        private readonly NamedPipeConnectionContext _connection;

        public NamedPipeReader
        (
            PipeReader reader,
            NamedPipeConnectionContext connection
        )
            : base(reader)
        {
            this._connection = connection;
        }

        public override void Complete(Exception? exception = null)
        {
            this._connection.InputReaderCompleted();
            base.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            this._connection.InputReaderCompleted();

            //could dispose stream
            //does a flush
            await this._reader.CompleteAsync(exception);

            try
            {
                await this._connection.AwaitReceiveTask();
            }
            catch
            { }
        }

        //public override async ValueTask<ReadResult> ReadAsync
        //(
        //    CancellationToken cancellationToken = default
        //)
        //{
        //    try
        //    {
        //        return await this._reader.ReadAsync(cancellationToken);
        //    }
        //    catch (IOException ex)
        //    {
        //        await this._connection.CompleteOutputAsync(ex);
        //        throw;
        //    }
        //}
    }
}
