using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using NippyWard.OpenSSL.SSL;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Tls
{
    internal class TlsServerConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly ILogger? _logger;
        private readonly SslOptions _sslOptions;

        //be aware that this will only get disposed by GC
        private readonly SslContext _sslContext;

        public TlsServerConnectionMiddleware
        (
            ConnectionDelegate next,
            SslOptions sslOptions,
            ILogger? logger = null
        )
        {
            this._next = next;
            this._logger = logger;
            this._sslOptions = sslOptions;

            this._sslContext = SslContext.CreateSslContext
            (
                sslOptions,
                true
            );
        }

        public async Task OnConnectionAsync(ConnectionContext context)
        {
            IDuplexPipe oldPipe = context.Transport;

            try
            {
                this._logger?.DebugLog(context.ConnectionId, "Activating TLS transport");

                using (TlsPipe pipe = await TlsPipe.AuthenticateAsServerAsync
                (
                    context.ConnectionId,
                    oldPipe.Input,
                    oldPipe.Output,
                    this._sslContext,
                    this._sslOptions.Pool,
                    this._logger,
                    context.ConnectionClosed
                ))
                {
                    context.Features.Set<ITlsConnectionFeature>(pipe);
                    context.Features.Set<ITlsHandshakeFeature>(pipe);

                    context.Transport = pipe;

                    await _next(context);
                }
            }
            finally
            {
                this._logger?.DebugLog(context.ConnectionId, "Deactivating TLS transport");
                context.Transport = oldPipe;
            }
        }
    }
}
