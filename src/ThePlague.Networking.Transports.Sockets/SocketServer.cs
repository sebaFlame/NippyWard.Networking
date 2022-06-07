using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

using ThePlague.Networking.Logging;

namespace ThePlague.Networking.Transports.Sockets
{
    /*TODO:
     * Add Pipe pooling as an alternative to PipeOptions
     */
    /// <summary>
    /// Represents a multi-client socket-server capable of dispatching pipeline clients
    /// </summary>
    public class SocketServer
        : IConnectionListener,
            IDisposable
    {
        public EndPoint EndPoint => this._endpoint;

        private readonly EndPoint _endpoint;
        private Socket? _listenerSocket;
        private PipeOptions? _sendOptions;
        private PipeOptions? _receiveOptions;
        private readonly IFeatureCollection? _serverFeatureCollection;
        private readonly ILogger? _logger;
        private readonly Func<string>? _createName;
        private readonly SocketAwaitableEventArgs _acceptEventArg;

        /// <summary>
        /// Create a new instance of a socket server
        /// </summary>
        public SocketServer
        (
            EndPoint endPoint,
            IFeatureCollection? serverFeatureCollection = null,
            Func<string>? createName = null,
            ILogger? logger = null
        )
        {
            this._endpoint = endPoint;
            this._createName = createName;
            this._logger = logger;
            this._serverFeatureCollection = serverFeatureCollection;
            this._acceptEventArg = new SocketAwaitableEventArgs(PipeScheduler.ThreadPool, logger);
        }

        /// <summary>
        /// Start listening as a server
        /// </summary>
        public void Bind
        (
            int listenBacklog = 20,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
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

            this.TraceLog("binding");

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
            PipeOptions? sendOptions,
            PipeOptions? receiveOptions
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
            this.TraceLog("stopping");

            Socket? socket = this._listenerSocket;
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

        public async ValueTask<ConnectionContext?> AcceptAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            CancellationTokenRegistration reg = cancellationToken.UnsafeRegister((s) => ((SocketServer?)s!).Stop(), this);

            try
            {
                SocketAwaitableEventArgs args = this._acceptEventArg;
                Socket socket = this._listenerSocket!;
                Socket clientSocket;

                ValueTask<Socket> socketTask = this._acceptEventArg.AcceptAsync(socket, cancellationToken);

                if(socketTask.IsCompletedSuccessfully)
                {
                    clientSocket = socketTask.Result;
                }
                else
                {
                    clientSocket = await socketTask;
                }

                this.TraceLog($"new connection: {clientSocket.RemoteEndPoint}");

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
                    name: this._createName is null ? nameof(SocketServer) : this._createName(),
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

        [Conditional("TRACELOG")]
        private void TraceLog(string message, [CallerFilePath] string? file = null, [CallerMemberName] string? caller = null, [CallerLineNumber] int lineNumber = 0)
        {
#if TRACELOG
            this._logger?.TraceLog($"SocketServer ({this._endpoint})", message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");
#endif
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
