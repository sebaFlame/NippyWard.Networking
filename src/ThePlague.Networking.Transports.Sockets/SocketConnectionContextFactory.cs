using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public class SocketConnectionContextFactory : IConnectionFactory
    {
        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default
        )
            => SocketConnectionContext.ConnectAsync(endpoint);
    }
}
