using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public class SocketConnectionContextListenerFactory
        : IConnectionListenerFactory
    {
        public ValueTask<IConnectionListener> BindAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            SocketServer server = new SocketServer(endpoint);
            server.Bind();
            return ValueTask.FromResult<IConnectionListener>(server);
        }
    }
}
