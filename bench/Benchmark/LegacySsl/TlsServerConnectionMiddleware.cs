using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using ThePlague.Networking.Pipelines;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace Benchmark.LegacySsl
{
    // code copied from https://github.com/davidfowl/BedrockFramework/
    internal class TlsServerConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly TlsOptions _options;
        private readonly ILogger _logger;
        private readonly X509Certificate2 _certificate;

        public TlsServerConnectionMiddleware(ConnectionDelegate next, TlsOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;

            // capture the certificate now so it can't be switched after validation
            _certificate = options.LocalCertificate;

            _options = options;
            _logger = loggerFactory?.CreateLogger<TlsServerConnectionMiddleware>();
        }

        public Task OnConnectionAsync(ConnectionContext context)
        {
            return Task.Run(() => InnerOnConnectionAsync(context));
        }

        private async Task InnerOnConnectionAsync(ConnectionContext context)
        {
            bool certificateRequired;
            var feature = new TlsConnectionFeature();
            context.Features.Set<ITlsConnectionFeature>(feature);
            context.Features.Set<ITlsHandshakeFeature>(feature);

            IStreamFeature streamFeature = context.Features.Get<IStreamFeature>();
            Stream stream = streamFeature.Stream;

            SslStream sslStream = new SslStream
            (
                stream,
                leaveInnerStreamOpen: true,
                userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                {
                    return true;
                }
            );

            using (var cancellationTokeSource = new CancellationTokenSource(Debugger.IsAttached ? Timeout.InfiniteTimeSpan : _options.HandshakeTimeout))
            {
                try
                {
                    SslServerAuthenticationOptions sslOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = _options.SslProtocols,
                        CertificateRevocationCheckMode = _options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck
                    };

                    _options.OnAuthenticateAsServer?.Invoke(context, sslOptions);

                    await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationTokeSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug(2, "Authentication timed out");
                    await sslStream.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is AuthenticationException)
                {
                    _logger?.LogDebug(1, ex, "Authentication failed");
                    await sslStream.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }

            IDuplexPipe sslDuplexPipe = new DuplexPipe(PipeReader.Create(sslStream), PipeWriter.Create(sslStream));

            feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.Protocol;
            feature.LocalCertificate = ConvertToX509Certificate2(sslStream.LocalCertificate);
            feature.RemoteCertificate = ConvertToX509Certificate2(sslStream.RemoteCertificate);
            feature.CipherAlgorithm = sslStream.CipherAlgorithm;
            feature.CipherStrength = sslStream.CipherStrength;
            feature.HashAlgorithm = sslStream.HashAlgorithm;
            feature.HashStrength = sslStream.HashStrength;
            feature.KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm;
            feature.KeyExchangeStrength = sslStream.KeyExchangeStrength;
            feature.Protocol = sslStream.SslProtocol;

            IDuplexPipe originalTransport = context.Transport;

            try
            {
                context.Transport = sslDuplexPipe;

                // Disposing the stream will dispose the sslDuplexPipe
                await using (sslStream)
                {
                    await _next(context).ConfigureAwait(false);
                    // Dispose the inner stream (SslDuplexPipe) before disposing the SslStream
                    // as the duplex pipe can hit an ODE as it still may be writing.
                }
            }
            finally
            {
                // Restore the original so that it gets closed appropriately
                context.Transport = originalTransport;
            }
        }

        private static X509Certificate2 ConvertToX509Certificate2(X509Certificate certificate)
        {
            if (certificate is null)
            {
                return null;
            }

            return certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        }
    }
}
