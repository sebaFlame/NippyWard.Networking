using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Logging;
using OpenSSL.Core.SSL;
using OpenSSL.Core.X509;
using OpenSSL.Core.Keys;

namespace ThePlague.Networking.Tls
{
    public static class TlsExtensions
    {
        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, SslOptions sslOptions)
        {
            ILogger logger = connectionBuilder.ApplicationServices.CreateLogger<TlsClientConnectionMiddleware>();
            return connectionBuilder.Use(next => new TlsClientConnectionMiddleware(next, sslOptions, logger).OnConnectionAsync);
        }

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder)
            => connectionBuilder.UseClientTls(SslOptions._Default);

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, SslStrength sslStrength)
            => connectionBuilder.UseClientTls(new SslOptions(sslStrength));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, SslProtocol sslProtocol)
            => connectionBuilder.UseClientTls(new SslOptions(sslProtocol));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, SslProtocol sslProtocol, IEnumerable<string> allowedCiphers)
            => connectionBuilder.UseClientTls(new SslOptions(sslProtocol, allowedCiphers));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, RemoteCertificateValidationHandler remoteValidation)
            => connectionBuilder.UseClientTls(new SslOptions(remoteValidation));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, X509Store caStore)
            => connectionBuilder.UseClientTls(new SslOptions(caStore));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key)
            => connectionBuilder.UseClientTls(new SslOptions(certificate, key));

        public static IConnectionBuilder UseClientTls(this IConnectionBuilder connectionBuilder, ClientCertificateCallbackHandler clientCertificateCallback)
            => connectionBuilder.UseClientTls(new SslOptions(clientCertificateCallback));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, SslOptions sslOptions)
        {
            ILogger logger = connectionBuilder.ApplicationServices.CreateLogger<TlsServerConnectionMiddleware>();
            return connectionBuilder.Use(next => new TlsServerConnectionMiddleware(next, sslOptions, logger).OnConnectionAsync);
        }

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key, X509Store caStore)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key, caStore));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key, RemoteCertificateValidationHandler remoteCertificateValidationHandler)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key, remoteCertificateValidationHandler));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key, SslStrength sslStrength)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key, sslStrength));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key, SslProtocol sslProtocol)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key, sslProtocol));

        public static IConnectionBuilder UseServerTls(this IConnectionBuilder connectionBuilder, X509Certificate certificate, PrivateKey key, SslProtocol sslProtocol, IEnumerable<string> allowedCiphers)
            => connectionBuilder.UseServerTls(new SslOptions(certificate, key, sslProtocol, allowedCiphers));
    }
}
