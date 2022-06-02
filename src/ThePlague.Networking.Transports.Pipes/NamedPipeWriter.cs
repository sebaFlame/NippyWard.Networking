using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO;

using System.IO.Pipelines;

namespace ThePlague.Networking.Transports.Pipes
{
    internal sealed class NamedPipeWriter : WrappedWriter
    {
        private readonly NamedPipeConnectionContext _connection;

        public NamedPipeWriter
        (
            PipeWriter writer,
            NamedPipeConnectionContext connection
        )
            : base(writer)
        {
            this._connection = connection;
        }

        public override void Complete(Exception exception = null)
        {
            this._connection.OutputWriterCompleted();
            this._writer.Complete(exception);
        }

        public override async ValueTask CompleteAsync(Exception exception = null)
        {
            this._connection.OutputWriterCompleted();

            try
            {
                //could dispose stream
                //does a flush
                await this._writer.CompleteAsync(exception);
            }
            catch
            { }

            await this._connection.AwaitSendTask();
        }

        //public override async ValueTask<FlushResult> FlushAsync
        //(
        //    CancellationToken cancellationToken = default
        //)
        //{ 
        //    try
        //    {
        //        return await this._writer.FlushAsync(cancellationToken);
        //    }
        //    catch(IOException ex)
        //    {
        //        await this._connection.CompleteInputAsync(ex);
        //        throw;
        //    }
        //}
    }
}
