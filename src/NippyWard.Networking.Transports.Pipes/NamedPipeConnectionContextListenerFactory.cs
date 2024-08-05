using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Connections;

namespace NippyWard.Networking.Transports.Pipes
{
    public class NamedPipeConnectionListenerFactory : IConnectionListenerFactory
    {
        private readonly IFeatureCollection? _featureCollection;
        private readonly ILogger? _logger;
        private readonly Func<string>? _createName;
        private readonly PipeOptions? _sendOptions;
        private readonly PipeOptions? _receiveOptions;

        public NamedPipeConnectionListenerFactory
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

        public ValueTask<IConnectionListener> BindAsync
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

            NamedPipeServer server = new NamedPipeServer
            (
                namedPipeEndPoint,
                this._featureCollection,
                this._createName,
                this._logger
            );

            server.Bind
            (
                sendPipeOptions: this._sendOptions,
                receivePipeOptions: this._receiveOptions
            );

            return new ValueTask<IConnectionListener>(server);
        }
    }
}