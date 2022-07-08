using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;
using NippyWard.Networking.Transports.Sockets;

namespace NippyWard.Networking.Transports.Proxy
{
    public class ProxyConnectionFactory : IConnectionFactory
    {
        private readonly Uri _proxyUri;
        private readonly NetworkCredential? _networkCredential;
        private readonly IFeatureCollection? _featureCollection;
        private readonly Func<string>? _createName;
        private readonly PipeOptions? _sendOptions;
        private readonly PipeOptions? _receiveOptions;
        private readonly ILogger? _logger;

        public ProxyConnectionFactory
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

        public ProxyConnectionFactory
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

        public ProxyConnectionFactory
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

        public async ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default
        )
        {
            if(!(endpoint is IPEndPoint ipEndpoint))
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

            this._logger?.DebugLog(this.GetType().Name, "Sending CONNECT command to proxy");

            //connect to remote address
            await Socks5Helper.WriteSocks5ProxyCommand
            (
                ProxyCommand.CONNECT,
                writer,
                ipEndpoint,
                this._logger
            );

            //read reply from proxy
            IPEndPoint localEndpoint = await Socks5Helper.VerifySocks5Command
            (
                reader,
                this._logger
            );

            //set the correct local endpoint
            context.LocalEndPoint = localEndpoint;

            //set the correct remote endpoint
            context.RemoteEndPoint = endpoint;

            //return proxied context
            return context;
        }
    }
}
