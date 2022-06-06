using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public class NamedPipeConnectionFactory : IConnectionFactory
    {
        private readonly IFeatureCollection? _featureCollection;
        private readonly Func<string>? _createName;
        private readonly PipeOptions? _sendOptions;
        private readonly PipeOptions? _receiveOptions;
        private readonly ILogger? _logger;

        public NamedPipeConnectionFactory
        (
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null,
            IFeatureCollection? featureCollection = null,
            ILogger? logger = null
        )
        {
            this._createName = createName;
            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
            this._featureCollection = featureCollection;
            this._logger = logger;
        }

        public NamedPipeConnectionFactory(IFeatureCollection featureCollection)
        {
            this._featureCollection = featureCollection;
        }

        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            if(endpoint is not NamedPipeEndPoint namedPipeEndPoint)
            {
                throw new NotSupportedException
                (
                    $"{endpoint.GetType().Name} not supported"
                );
            }

            return NamedPipeConnectionContext.ConnectAsync
            (
                namedPipeEndPoint,
                sendPipeOptions: this._sendOptions,
                receivePipeOptions: this._receiveOptions,
                featureCollection: this._featureCollection,
                name: this._createName is null ? nameof(NamedPipeConnectionContext) : this._createName(),
                logger: this._logger
            );
        }
    }
}

