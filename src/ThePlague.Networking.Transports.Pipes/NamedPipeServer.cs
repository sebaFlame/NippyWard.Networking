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
    public class NamedPipeServer
        : IConnectionListener,
            IDisposable
    {
        public EndPoint EndPoint => this._endPoint;

        private StreamPipeWriterOptions _sendOptions;
        private StreamPipeReaderOptions _receiveOptions;
        private readonly NamedPipeEndPoint _endPoint;
        private readonly IFeatureCollection _serverFeatureCollection;

        private NamedPipeServerStream _inputStream;
        private NamedPipeServerStream _outputStream;

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

            this._outputStream = new NamedPipeServerStream
            (
                string.Concat(endpoint.PipeName, NamedPipeConnectionContext._OutputSuffix),
                PipeDirection.Out,
                maxAllowedServerInstances,
                pipeTransmissionMode,
                pipeOptions
            );

            this._inputStream = new NamedPipeServerStream
            (
                string.Concat(endpoint.PipeName, NamedPipeConnectionContext._InputSuffix),
                PipeDirection.In,
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
            NamedPipeServerStream output = this._outputStream;
            this._outputStream = null;

            if(output is null)
            {
                return;
            }

            try
            {
                output.Dispose();
            }
            catch { }

            NamedPipeServerStream input = this._inputStream;
            this._outputStream = null;

            if (input is null)
            {
                return;
            }

            try
            {
                input.Dispose();
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
                    //TODO: exception handling per stream
                    await Task.WhenAll
                    (
                        this._outputStream.WaitForConnectionAsync(cancellationToken),
                        this._inputStream.WaitForConnectionAsync(cancellationToken)
                    );

                    return new NamedPipeConnectionContext
                    (
                        this._endPoint,
                        this._outputStream,
                        this._inputStream,
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
