using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Transports.Sockets
{
    /// <summary>
    /// Represents a multi-client socket-server capable of dispatching pipeline clients
    /// </summary>
    public class SocketServer
        : IConnectionListener,
            IDisposable
    {
        public EndPoint EndPoint { get; private set; }

        private Socket _listenerSocket;
        private PipeOptions _sendOptions;
        private PipeOptions _receiveOptions;
        private readonly IFeatureCollection _serverFeatureCollection;
        private readonly ILogger _logger;

        /// <summary>
        /// Create a new instance of a socket server
        /// </summary>
        public SocketServer
        (
            EndPoint endPoint,
            ILogger logger = null
        )
        {
            this.EndPoint = endPoint;
            this._logger = logger;
        }

        /// <summary>
        /// Create a new instance of a socket server
        /// </summary>
        public SocketServer
        (
            EndPoint endPoint,
            IFeatureCollection serverFeatureCollection,
            ILogger logger = null
        )
            : this(endPoint, logger)
        {
            this._serverFeatureCollection = serverFeatureCollection;
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        public void Bind
        (
            int listenBacklog = 20,
            PipeOptions sendOptions = null,
            PipeOptions receiveOptions = null
        )
        {
            AddressFamily addressFamily =
                this.EndPoint.AddressFamily == AddressFamily.Unspecified
                    ? AddressFamily.InterNetwork
                    : this.EndPoint.AddressFamily;

            ProtocolType protocolType =
                addressFamily == AddressFamily.Unix
                    ? ProtocolType.Unspecified
                    : ProtocolType.Tcp;

            if (this._listenerSocket is not null)
            {
                throw new InvalidOperationException
                (
                    "Server is already running"
                );
            }

            Socket listener = new Socket
            (
                addressFamily,
                SocketType.Stream,
                protocolType
            );

            listener.Bind(this.EndPoint);
            listener.Listen(listenBacklog);

            //this.EndPoint = listener.LocalEndPoint;

            this._listenerSocket = listener;
            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        internal void Bind
        (
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => this.Bind
            (
                20,
                sendOptions,
                receiveOptions
            );

        /// <summary>
        /// Stop listening as a server
        /// </summary>
        public void Stop()
        {
            Socket socket = this._listenerSocket;
            this._listenerSocket = null;

            if(socket is not null)
            {
                try
                {
                    socket.Dispose();
                }
                catch { }
            }
        }

        public async ValueTask<ConnectionContext> AcceptAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            CancellationTokenRegistration reg = cancellationToken.Register((s) => ((SocketServer)s).Stop(), this, false);

            try
            {
                Socket clientSocket = await this._listenerSocket.AcceptAsync();

                SocketConnectionContext.SetRecommendedServerOptions
                (
                    clientSocket
                );

                return SocketConnectionContext.Create
                (
                    clientSocket,
                    sendPipeOptions: this._sendOptions,
                    receivePipeOptions: this._receiveOptions,
                    serverFeatureCollection: this._serverFeatureCollection,
                    logger: this._logger
                );
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return null;
            }
            catch (SocketException e)
                when (e.SocketErrorCode == SocketError.OperationAborted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return null;
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();

                throw;
            }
            finally
            {
                reg.Dispose();
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
