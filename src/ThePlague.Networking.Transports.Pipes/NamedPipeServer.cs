using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipes;

using Microsoft.AspNetCore.Connections;

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

        public NamedPipeServer
        (
            NamedPipeEndPoint endPoint
        )
        {
            this._endPoint = endPoint;
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
                    await this._stream
                        .WaitForConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);

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
