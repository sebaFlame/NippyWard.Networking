using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public class SocketConnectionContextFactory : IConnectionFactory
    {
        private IFeatureCollection _featureCollection;
        private readonly ILogger _logger;
        private Func<string> _createName;

        public SocketConnectionContextFactory(Func<string> createName, ILogger logger = null)
        {
            this._createName = createName;
            this._logger = logger;
        }

        public SocketConnectionContextFactory(ILogger logger = null)
            : this(() => string.Empty, logger)
        { }

        public SocketConnectionContextFactory(IFeatureCollection featureCollection)
        {
            this._featureCollection = featureCollection;
        }

        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default
        )
            => SocketConnectionContext.ConnectAsync
            (
                endpoint,
                featureCollection: this._featureCollection,
                name: this._createName(),
                logger: this._logger
            );
    }
}
