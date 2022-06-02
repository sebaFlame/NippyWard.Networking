using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Logging;

namespace ThePlague.Networking.Transports.Sockets
{
    public partial class SocketConnectionContext
    {
        /// <summary>
        /// Open a new or existing socket as a client
        /// </summary>
        public static ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            PipeOptions pipeOptions = null,
            Socket socket = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null,
            CancellationToken cancellationToken = default
        )
            => ConnectAsync
            (
                endpoint,
                connectionOptions,
                pipeOptions,
                pipeOptions,
                socket,
                featureCollection,
                name,
                logger,
                cancellationToken
            );

        /// <summary>
        /// Open a new or existing socket as a client
        /// </summary>
        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            PipeOptions sendPipeOptions = null,
            PipeOptions receivePipeOptions = null,
            Socket socket = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null,
            CancellationToken cancellationToken = default
        )
        {
            socket = await ConnectAsync
            (
                endpoint,
                connectionOptions,
                socket,
                name,
                logger,
                cancellationToken
            );

            TransportConnectionContext connection = Create
            (
                socket,
                connectionOptions,
                sendPipeOptions,
                receivePipeOptions,
                featureCollection,
                name,
                logger
            );

            connection.LocalEndPoint = socket.LocalEndPoint;
            connection.RemoteEndPoint = socket.RemoteEndPoint;

            return connection;
        }

        /// <summary>
        /// Open a new or existing socket as a client
        /// </summary>
        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            Pipe sendToSocket,
            Pipe receiveFromSocket,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            PipeScheduler sendScheduler = null,
            PipeScheduler receiveScheduler = null,
            Socket socket = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null,
            CancellationToken cancellationToken = default
        )
        {
            socket = await ConnectAsync
            (
                endpoint,
                connectionOptions,
                socket,
                name,
                logger,
                cancellationToken
            );

            TransportConnectionContext connection = Create
            (
                socket,
                sendToSocket,
                receiveFromSocket,
                connectionOptions,
                sendScheduler,
                receiveScheduler,
                featureCollection,
                name,
                logger
            );

            connection.LocalEndPoint = socket.LocalEndPoint;
            connection.RemoteEndPoint = socket.RemoteEndPoint;

            return connection;
        }

        private static async Task<Socket> ConnectAsync
        (
            EndPoint endpoint,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            Socket socket = null,
            string name = null,
            ILogger logger = null,
            CancellationToken cancellationToken = default
        )
        {
            if (socket is null)
            {
                AddressFamily addressFamily =
                    endpoint.AddressFamily == AddressFamily.Unspecified
                        ? AddressFamily.InterNetwork
                        : endpoint.AddressFamily;

                ProtocolType protocolType =
                    addressFamily == AddressFamily.Unix
                        ? ProtocolType.Unspecified
                        : ProtocolType.Tcp;

                socket = new Socket
                (
                    addressFamily,
                    SocketType.Stream,
                    protocolType
                );
            }

            SetRecommendedClientOptions(socket);

            logger?.TraceLog(name, $"connecting to {endpoint}");

            using (SocketAwaitableEventArgs args = new SocketAwaitableEventArgs
            (
                (connectionOptions & SocketConnectionOptions.InlineConnect) == 0
                    ? PipeScheduler.ThreadPool
                    : PipeScheduler.Inline,
                logger
            ))
            {
                args.RemoteEndPoint = endpoint;

                ValueTask connectTask = args.ConnectAsync(socket, cancellationToken);
                if (!connectTask.IsCompletedSuccessfully)
                {
                    await connectTask;
                }
            }

            return socket;
        }
    }
}
