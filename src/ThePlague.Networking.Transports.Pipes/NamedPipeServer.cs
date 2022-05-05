using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipes;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace ThePlague.Networking.Transports.Pipes
{
    /// <summary>
    /// Represents a multi-client socket-server capable of dispatching pipeline clients
    /// </summary>
    internal class NamedPipeServer
        : IConnectionListener,
            IDisposable
    {
        public EndPoint EndPoint => this._endPoint;

        private StreamPipeWriterOptions _sendOptions;
        private StreamPipeReaderOptions _receiveOptions;
        private NamedPipeServerStream _stream;
        private readonly NamedPipeEndPoint _endPoint;
        private readonly IFeatureCollection _serverFeatureCollection;

        public NamedPipeServer
        (
            NamedPipeEndPoint endPoint
        )
        {
            this._endPoint = endPoint;
        }

        public NamedPipeServer
        (
            NamedPipeEndPoint endPoint,
            IFeatureCollection serverFeatureCollection
        )
            : this(endPoint)
        {
            this._serverFeatureCollection = serverFeatureCollection;
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        internal void Bind
        (
            PipeDirection pipeDirection = PipeDirection.InOut,
            int maxAllowedServerInstances
                = NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode pipeTransmissionMode
                = PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions pipeOptions
                = System.IO.Pipes.PipeOptions.Asynchronous,
            StreamPipeWriterOptions sendOptions = null,
            StreamPipeReaderOptions receiveOptions = null
        )
        {
            NamedPipeEndPoint endpoint = this._endPoint;

            this._stream = new NamedPipeServerStream
            (
                endpoint.PipeName,
                pipeDirection,
                maxAllowedServerInstances,
                pipeTransmissionMode,
                pipeOptions
            );

            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
        }

        /// <summary>
        /// Stop listening as a server
        /// </summary>
        public void Stop()
        {
            NamedPipeServerStream stream = this._stream;
            this._stream = null;

            if(stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch { }
        }

        public async ValueTask<ConnectionContext> AcceptAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            while(true)
            {
                try
                {
                    await this._stream.WaitForConnectionAsync(cancellationToken);

                    return new NamedPipeConnectionContext
                    (
                        this._stream,
                        this._endPoint,
                        this._sendOptions,
                        this._receiveOptions
                    );
                }
                catch(ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    return null;
                }
            }
        }

        public ValueTask UnbindAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            this.Stop();
            return default;
        }

        /// <summary>
        /// Release any resources associated with this instance
        /// </summary>
        public void Dispose()
            => this.Stop();

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return default;
        }
    }
}
