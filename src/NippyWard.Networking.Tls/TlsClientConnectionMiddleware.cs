﻿using System;
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
    public class TlsClientConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly SslOptions _sslOptions;
        private readonly ILogger? _logger;

        public TlsClientConnectionMiddleware
        (
            ConnectionDelegate next,
            SslOptions sslOptions,
            ILogger? logger = null
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
                this._logger?.DebugLog(context.ConnectionId, "Activating TLS transport");
                using (TlsPipe pipe = await TlsPipe.AuthenticateAsClientAsync
                (
                    context.ConnectionId,
                    oldPipe.Input,
                    oldPipe.Output,
                    this._sslOptions,
                    this._sslOptions.Pool,
                    this._logger,
                    context.ConnectionClosed
                ))
                {
                    context.Features.Set<ITlsConnectionFeature>(pipe);
                    context.Features.Set<ITlsHandshakeFeature>(pipe);

                    context.Transport = pipe;

                    try
                    {
                        await this._next(context);

                        await pipe.ShutdownAsync();
                    }
                    finally
                    {
                        context.Features.Set<ITlsConnectionFeature>(null);
                        context.Features.Set<ITlsHandshakeFeature>(null);
                    }
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
