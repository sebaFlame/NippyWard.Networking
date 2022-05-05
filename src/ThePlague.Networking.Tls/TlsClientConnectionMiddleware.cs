using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using OpenSSL.Core.SSL;

namespace ThePlague.Networking.Tls
{
    public class TlsClientConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly SslOptions _sslOptions;
        private readonly ILogger _logger;

        public TlsClientConnectionMiddleware
        (
            ConnectionDelegate next,
            SslOptions sslOptions,
            ILogger logger
        )
        {
            this._next = next;
            this._sslOptions = sslOptions;
            this._logger = logger;
        }

        public async Task OnConnectionAsync(ConnectionContext context)
        {
            IDuplexPipe oldPipe = context.Transport;

            try
            {
                using (TlsPipe pipe = new TlsPipe(oldPipe.Input, oldPipe.Output, this._logger))
                {
                    context.Features.Set<ITlsConnectionFeature>(pipe);
                    context.Features.Set<ITlsHandshakeFeature>(pipe);

                    this._logger.LogDebug("Activating TLS transport");
                    await pipe.AuthenticateAsClientAsync
                    (
                        this._sslOptions,
                        context.ConnectionClosed
                    );

                    context.Transport = pipe;

                    await _next(context);
                }
            }
            finally
            {
                this._logger.LogDebug("Deactivating TLS transport");
                context.Transport = oldPipe;
            }
        }
    }
}
