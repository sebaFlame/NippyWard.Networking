﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using OpenSSL.Core.SSL;
using ThePlague.Networking.Logging;

#nullable enable

namespace ThePlague.Networking.Tls
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
                using (TlsPipe pipe = new TlsPipe
                (
                    context.ConnectionId,
                    oldPipe.Input,
                    oldPipe.Output,
                    this._logger,
                    this._sslOptions.Pool
                ))
                {
                    context.Features.Set<ITlsConnectionFeature>(pipe);
                    context.Features.Set<ITlsHandshakeFeature>(pipe);

                    this._logger?.DebugLog(context.ConnectionId, "Activating TLS transport");
                    await pipe.AuthenticateAsServerAsync
                    (
                        this._sslContext,
                        context.ConnectionClosed
                    );

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