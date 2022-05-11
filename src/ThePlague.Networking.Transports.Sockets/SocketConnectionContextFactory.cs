using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public class SocketConnectionContextFactory : IConnectionFactory
    {
        private IFeatureCollection _featureCollection;
        private readonly Func<string> _createName;
        private readonly PipeOptions _sendOptions;
        private readonly PipeOptions _receiveOptions;
        private readonly ILogger _logger;

        public SocketConnectionContextFactory
        (
            Func<string> createName = null,
            PipeOptions sendOptions = null,
            PipeOptions receiveOptions = null,
            IFeatureCollection featureCollection = null,
            ILogger logger = null
        )
        {
            this._createName = createName;
            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
            this._featureCollection = featureCollection;
            this._logger = logger;
        }

        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default
        )
            => SocketConnectionContext.ConnectAsync
            (
                endpoint,
                sendPipeOptions: this._sendOptions,
                receivePipeOptions: this._receiveOptions,
                featureCollection: this._featureCollection,
                name: this._createName(),
                logger: this._logger
            );
    }
}
