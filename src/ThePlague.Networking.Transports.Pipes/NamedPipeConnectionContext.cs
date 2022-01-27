using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

using ThePlague.Networking.Pipelines;

namespace ThePlague.Networking.Transports.Pipes
{
    internal class NamedPipeConnectionContext
        : ConnectionContext,
            IConnectionIdFeature,
            IConnectionTransportFeature,
            IConnectionItemsFeature,
            IConnectionLifetimeFeature
    {
        public override string ConnectionId { get; set; }
        public override IFeatureCollection Features { get; }
        public override IDictionary<object, object> Items { get; set; }
        public override IDuplexPipe Transport { get; set; }

        private readonly PipeStream _stream;
        private readonly IDuplexPipe _pipe;

        internal NamedPipeConnectionContext
        (
            PipeStream stream,
            NamedPipeEndPoint endPoint,
            StreamPipeWriterOptions sendOptions = null,
            StreamPipeReaderOptions receiveOptions = null
        )
        {
            this._stream = stream;
            this.ConnectionId = Guid.NewGuid().ToString();
            this.RemoteEndPoint = endPoint;

            this._pipe = new DuplexPipe
            (
                PipeReader.Create(stream, receiveOptions),
                PipeWriter.Create(stream, sendOptions)
            );

            this.Transport = this._pipe;

            this.Features = new FeatureCollection();
            this.Items = new ConnectionItems();
        }

        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            NamedPipeEndPoint endPoint,
            PipeDirection pipeDirection = PipeDirection.InOut,
            System.IO.Pipes.PipeOptions pipeOptions
                = System.IO.Pipes.PipeOptions.Asynchronous,
            StreamPipeWriterOptions sendOptions = null,
            StreamPipeReaderOptions receiveOptions = null
        )
        {
            NamedPipeClientStream stream = new NamedPipeClientStream
            (
                endPoint.PipeName,
                endPoint.ServerName,
                pipeDirection,
                pipeOptions
            );

            await stream.ConnectAsync().ConfigureAwait(false);

            return new NamedPipeConnectionContext
            (
                stream,
                endPoint,
                sendOptions,
                receiveOptions
            );
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            this._pipe.Input.Complete(abortReason);
            this._pipe.Output.Complete(abortReason);

            base.Abort(abortReason);
        }

        public void Dispose()
        {
            this._pipe.Input.Complete();
            this._pipe.Output.Complete();

            this._stream.Dispose();
        }

        public override ValueTask DisposeAsync()
        {
            this.Dispose();
            return base.DisposeAsync();
        }
    }
}
