using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;
using NippyWard.Networking.Transports.Sockets;

namespace NippyWard.Networking.Transports.Proxy
{
    public class ProxyConnectionListenerFactory
        : IConnectionListenerFactory
    {
        private readonly Uri _proxyUri;
        private readonly NetworkCredential? _networkCredential;
        private readonly IFeatureCollection? _featureCollection;
        private readonly ILogger? _logger;
        private readonly Func<string>? _createName;
        private readonly PipeOptions? _sendOptions;
        private readonly PipeOptions? _receiveOptions;

        public ProxyConnectionListenerFactory
        (
            Uri proxyUri,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null,
            IFeatureCollection? featureCollection = null,
            ILogger? logger = null
        )
        {
            this._proxyUri = proxyUri;
            this._createName = createName;
            this._sendOptions = sendOptions;
            this._receiveOptions = receiveOptions;
            this._featureCollection = featureCollection;
            this._logger = logger;

            //generate credentials from Uri
            ICredentials proxyCredentials = ProxyCredential.TryCreate(proxyUri);
            this._networkCredential
                = proxyCredentials.GetCredential(proxyUri, proxyUri.Scheme);
        }

        public ProxyConnectionListenerFactory
        (
            string proxyHost,
            ushort proxyPort = Socks5Helper._SocksDefaultPort,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null,
            IFeatureCollection? featureCollection = null,
            ILogger? logger = null
        )
            : this
            (
                proxyUri: new Uri
                (
                    string.Concat
                    (
                        "socks5",
                        "://",
                        proxyHost,
                        ":",
                        proxyPort
                    )
                ),
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                featureCollection: featureCollection,
                logger: logger
            )
        { }

        public ProxyConnectionListenerFactory
        (
            string userName,
            string password,
            string proxyHost,
            ushort proxyPort = Socks5Helper._SocksDefaultPort,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null,
            IFeatureCollection? featureCollection = null,
            ILogger? logger = null
        )
            : this
            (
                proxyUri: new Uri
                (
                    string.Concat
                    (
                        "socks5",
                        "://",
                        Uri.EscapeDataString(userName),
                        ":",
                        Uri.EscapeDataString(password),
                        "@",
                        proxyHost,
                        ":",
                        proxyPort
                    )
                ),
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                featureCollection: featureCollection,
                logger: logger
            )
        { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="endpoint">A remote (!) endpoint on the proxy</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public async ValueTask<IConnectionListener> BindAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            if (!(endpoint is IPEndPoint ipEndpoint))
            {
                throw new NotSupportedException($"endpoint needs to be an {nameof(IPEndPoint)}");
            }

            IPEndPoint proxyEndpoint = await Socks5Helper.ResolveProxyUri(this._proxyUri);

            //connect to the proxy
            ConnectionContext context = await SocketConnectionContext.ConnectAsync
            (
                proxyEndpoint,
                sendPipeOptions: this._sendOptions,
                receivePipeOptions: this._receiveOptions,
                featureCollection: this._featureCollection,
                name: this._createName is null ? nameof(SocketConnectionContext) : this._createName(),
                logger: this._logger
            );

            PipeReader reader = context.Transport.Input;
            PipeWriter writer = context.Transport.Output;

            //authenticate to proxy
            await Socks5Helper.AuthenticateSocks5ProxyAsync
            (
                writer,
                reader,
                this._networkCredential,
                this._logger
            );

            this._logger?.DebugLog(this.GetType().Name, "Sending BIND command to proxy");

            //connect to remote address
            await Socks5Helper.WriteSocks5ProxyCommand
            (
                ProxyCommand.BIND,
                writer,
                ipEndpoint,
                this._logger
            );

            this._logger?.DebugLog(this.GetType().Name, "Waiting on local endpoint BIND reply from proxy");

            //read bind reply from proxy
            IPEndPoint localEndpoint = await Socks5Helper.VerifySocks5Command
            (
                reader,
                this._logger
            );

            //and check if it's the correct endpoint it bound to
            //if (!localEndpoint.Equals(endpoint))
            //{
            //    throw new ProxyException($"Remote endpoint not valid. Expected {endpoint}, got {localEndpoint}");
            //}

            //store the endpoint for application usage
            context.LocalEndPoint = localEndpoint;

            return new ProxyServer
            (
                localEndpoint,
                context,
                this._logger
            );
        }
    }
}
