using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;

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
            PipeOptions pipeOptions = null,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            Socket socket = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null
        )
            => ConnectAsync
            (
                endpoint,
                pipeOptions,
                pipeOptions,
                connectionOptions,
                socket,
                featureCollection,
                name,
                logger
            );

        /// <summary>
        /// Open a new or existing socket as a client
        /// </summary>
        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions,
            SocketConnectionOptions connectionOptions = SocketConnectionOptions.None,
            Socket socket = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null
        )
        {
            AddressFamily addressFamily =
                endpoint.AddressFamily == AddressFamily.Unspecified
                    ? AddressFamily.InterNetwork
                    : endpoint.AddressFamily;

            ProtocolType protocolType =
                addressFamily == AddressFamily.Unix
                    ? ProtocolType.Unspecified
                    : ProtocolType.Tcp;

            if(socket is null)
            {
                socket = new Socket
                (
                    addressFamily,
                    SocketType.Stream,
                    protocolType
                );
            }

            if(sendPipeOptions is null)
            {
                sendPipeOptions = PipeOptions.Default;
            }

            if(receivePipeOptions is null)
            {
                receivePipeOptions = PipeOptions.Default;
            }

            SetRecommendedClientOptions(socket);

            logger.TraceLog(name, $"connecting to {endpoint}");

            using(SocketAwaitableEventArgs args = new SocketAwaitableEventArgs
            (
                (connectionOptions & SocketConnectionOptions.InlineConnect) == 0
                    ? PipeScheduler.ThreadPool
                    : null
            ))
            {
                args.RemoteEndPoint = endpoint;

                if(!socket.ConnectAsync(args))
                {
                    args.Complete();
                }

                await args;
            }

            SocketConnectionContext connection = Create
            (
                socket,
                sendPipeOptions,
                receivePipeOptions,
                connectionOptions,
                featureCollection,
                name,
                logger
            );

            connection.LocalEndPoint = socket.LocalEndPoint;
            connection.RemoteEndPoint = socket.RemoteEndPoint;

            return connection;
        }
    }
}
