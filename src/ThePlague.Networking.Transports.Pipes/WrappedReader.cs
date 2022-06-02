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
    internal sealed class WrappedReader : PipeReader
    {
        private readonly PipeReader _reader;
        private readonly NamedPipeConnectionContext _connection;

        public WrappedReader
        (
            PipeReader reader,
            NamedPipeConnectionContext connection
        )
        {
            this._reader = reader;
            this._connection = connection;
        }

        public override void Complete(Exception exception = null)
        {
            this._reader.Complete(exception);

            try
            {
                this._connection._InputStream.Dispose();
            }
            catch
            { }
        }

        public override async ValueTask CompleteAsync(Exception exception = null)
        {
            try
            {
                //could dispose stream
                //does a flush
                await this._reader.CompleteAsync(exception);
            }
            catch
            { }

            try
            {
                await this._connection._InputStream.DisposeAsync();
            }
            catch
            { }
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

        public override async ValueTask<ReadResult> ReadAsync
        (
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                return await this._reader.ReadAsync(cancellationToken);
            }
            catch(IOException ex)
            {
                this._connection.CompleteOutput(ex);
                throw;
            }
        }

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
