using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Sockets
{
    /// <summary>
    /// Represents a multi-client socket-server capable of dispatching pipeline clients
    /// </summary>
    internal class SocketServer
        : IConnectionListener,
            IDisposable
    {
        public EndPoint EndPoint { get; private set; }

        private Socket _listenerSocket;
        private PipeOptions _sendOptions;
        private PipeOptions _receiveOptions;

        /// <summary>
        /// Create a new instance of a socket server
        /// </summary>
        public SocketServer
        (
            EndPoint endPoint
        )
        {
            this.EndPoint = endPoint;
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        internal void Bind
        (
            AddressFamily addressFamily = AddressFamily.InterNetwork,
            SocketType socketType = SocketType.Stream,
            ProtocolType protocolType = ProtocolType.Tcp,
            int listenBacklog = 20,
            PipeOptions sendOptions = null,
            PipeOptions receiveOptions = null
        )
        {
            if(this._listenerSocket is not null)
            {
                throw new InvalidOperationException
                (
                    "Server is already running"
                );
            }

            Socket listener = new Socket
            (
                addressFamily,
                socketType,
                protocolType
            );

            this.EndPoint = listener.LocalEndPoint;

            listener.Bind(this.EndPoint);
            listener.Listen(listenBacklog);

            this._listenerSocket = listener;
            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        internal void Bind
        (
            AddressFamily addressFamily,
            SocketType socketType,
            ProtocolType protocolType,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => this.Bind
            (
                addressFamily,
                socketType,
                protocolType,
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
            while(true)
            {
                try
                {
                    Socket clientSocket
                        = await this._listenerSocket.AcceptAsync();

                    SocketConnection.SetRecommendedServerOptions
                    (
                        clientSocket
                    );

                    SocketConnection socketConnection = SocketConnection.Create
                    (
                        clientSocket,
                        sendPipeOptions: this._sendOptions,
                        receivePipeOptions: this._receiveOptions
                    );
                }
                catch(ObjectDisposedException)
                {
                    return null;
                }
                catch(SocketException e)
                    when(e.SocketErrorCode == SocketError.OperationAborted)
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
