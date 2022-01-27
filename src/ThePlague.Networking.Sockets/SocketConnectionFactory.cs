using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Sockets
{
    public class SocketConnectionFactory : IConnectionFactory
    {
        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default
        )
            => SocketConnection.ConnectAsync(endpoint);
    }
}
