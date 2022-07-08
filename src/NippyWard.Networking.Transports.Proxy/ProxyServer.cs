using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Transports.Proxy
{
    public class ProxyServer : IConnectionListener
    {
        public EndPoint EndPoint => this._endpoint;

        private readonly EndPoint _endpoint;
        private ConnectionContext _connectionContext;
        private readonly ILogger? _logger;

        private ConnectionContext? _returned;

        /// <summary>
        /// Create a new instance of a socket server
        /// </summary>
        public ProxyServer
        (
            EndPoint endpoint,
            ConnectionContext connectionContext,
            ILogger? logger = null
        )
        {
            this._endpoint = endpoint;
            this._connectionContext = connectionContext;
            this._logger = logger;
        }

        public async ValueTask<ConnectionContext?> AcceptAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            if(this._returned is not null)
            {
                throw new InvalidOperationException($"{nameof(ProxyServer)} can only listen to a single connection");
            }

            PipeReader reader = this._connectionContext.Transport.Input;
            PipeWriter writer = this._connectionContext.Transport.Output;

            this._logger?.DebugLog(this.GetType().Name, "Waiting on remote endpoint BIND reply from proxy");

            //read bind reply from proxy containing the remote IP
            IPEndPoint remoteEndpoint = await Socks5Helper.VerifySocks5Command
            (
                reader,
                this._logger
            );

            //set the remote IP
            this._connectionContext.RemoteEndPoint = remoteEndpoint;

            return (this._returned = this._connectionContext);
        }

        public ValueTask UnbindAsync
        (
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            return default;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }
}
