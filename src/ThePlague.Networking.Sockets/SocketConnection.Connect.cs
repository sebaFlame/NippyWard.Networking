using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Sockets
{
    public partial class SocketConnection
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
            string name = null
        )
            => ConnectAsync
            (
                endpoint,
                pipeOptions,
                pipeOptions,
                connectionOptions,
                socket,
                name
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
            string name = null
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

            SocketConnection connection = Create
            (
                socket,
                sendPipeOptions,
                receivePipeOptions,
                connectionOptions,
                name
            );

            connection.LocalEndPoint = socket.LocalEndPoint;
            connection.RemoteEndPoint = socket.RemoteEndPoint;

            return connection;
        }
    }
}
