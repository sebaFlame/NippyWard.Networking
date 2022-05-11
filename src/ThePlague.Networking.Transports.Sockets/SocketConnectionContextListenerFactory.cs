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
    public class SocketConnectionContextListenerFactory
        : IConnectionListenerFactory
    {
        private readonly IFeatureCollection _featureCollection;
        private readonly ILogger _logger;
        private readonly Func<string> _createName;
        private readonly PipeOptions _sendOptions;
        private readonly PipeOptions _receiveOptions;

        public SocketConnectionContextListenerFactory
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

        public ValueTask<IConnectionListener> BindAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            SocketServer server = new SocketServer
            (
                endpoint,
                this._featureCollection,
                this._createName,
                this._logger
            );

            server.Bind
            (
                this._sendOptions,
                this._receiveOptions
            );

            return ValueTask.FromResult<IConnectionListener>(server);
        }
    }
}
