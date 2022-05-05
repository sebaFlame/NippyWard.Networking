using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public class SocketConnectionContextListenerFactory
        : IConnectionListenerFactory
    {
        private IFeatureCollection _featureCollection;
        private ILogger _logger;

        internal SocketConnectionContextListenerFactory(ILogger logger = null)
        {
            this._logger = logger;
        }

        public SocketConnectionContextListenerFactory(IFeatureCollection featureCollection)
        {
            this._featureCollection = featureCollection;
        }

        public ValueTask<IConnectionListener> BindAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            SocketServer server = new SocketServer(endpoint, this._featureCollection, this._logger);
            server.Bind();
            return ValueTask.FromResult<IConnectionListener>(server);
        }
    }
}
