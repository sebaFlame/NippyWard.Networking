using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Net.NetworkInformation;
using System.Net;
using System.Buffers;
using System.Text.Encodings;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;

using OpenSSL.Core;
using OpenSSL.Core.Keys;
using OpenSSL.Core.SSL;
using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.ASN1;
using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Tls;
using Benchmark.LegacySsl;
using System.Text;

namespace Benchmark
{
    public abstract class BaseSslBenchmark : BaseBenchmark
    {
        /* Defaults (you NEED to verify these to get -more- correct benchmarks results)
         * - Using OpenSsl server with Legacy client: AES128-GCM-SHA256 / TLS_RSA_WITH_AES_128_GCM_SHA256
         * - Using Lagacy server with Legacy client: TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 / TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
         */
        internal const string _Cipher = "ECDHE-RSA-AES256-GCM-SHA384";
        internal const SslProtocol _OpenSsslProtocol = SslProtocol.Tls12;
        internal const SslProtocols _LegacySslProtocl = SslProtocols.Tls12;

        protected static void InitializeCertificate
        (
            out OpenSSL.Core.X509.X509Certificate openSslCertificate,
            out PrivateKey openSslKey,
            out X509Certificate2 certificate
        )
        {
            var start = DateTime.Now;
            var end = start + TimeSpan.FromMinutes(10);

            //create key
            openSslKey = new RSAKey(2048);
            openSslKey.GenerateKey();

            //create certificate
            openSslCertificate = new OpenSSL.Core.X509.X509Certificate
            (
                openSslKey,
                "localhost",
                "localhost",
                start,
                end
            );

            //self sign certificate
            openSslCertificate.SelfSign(openSslKey, DigestType.SHA256);

            //create memorystream to write certificate to
            char[] certBuffer, keyBuffer;
            using (MemoryStream stream = new MemoryStream())
            {
                openSslCertificate.Write(stream, string.Empty, CipherType.NONE, FileEncoding.PEM);

                certBuffer = Encoding.UTF8.GetChars(stream.ToArray());

                //reset stream
                stream.Position = 0;
                stream.SetLength(0);

                openSslKey.Write(stream, string.Empty, CipherType.NONE, FileEncoding.PEM);

                keyBuffer = Encoding.UTF8.GetChars(stream.ToArray());
            }

            X509Certificate2 temp = X509Certificate2.CreateFromPem
            (
                certBuffer,
                keyBuffer
            );

            //workaround for "No credentials are available in the security package"
            //https://github.com/dotnet/runtime/issues/23749
            certificate = new X509Certificate2(temp.Export(X509ContentType.Pkcs12));
        }

        //"block" the ConnectionDelegate, so it does not dispose the ssl object
        private static IConnectionBuilder SetAwaitTask
        (
            IConnectionBuilder connectionBuilder,
            bool awaitTask
        )
            => awaitTask
            ? connectionBuilder
                .Use
                (
                    (next) =>
                    async (ConnectionContext ctx) =>
                    {
                        TaskCompletionSource doneTcs = (TaskCompletionSource)ctx.Items["done"]!;
                        TaskCompletionSource connectedTcs = (TaskCompletionSource)ctx.Items["connected"]!;

                        //connected
                        connectedTcs.SetResult();

                        //block
                        await doneTcs.Task;

                        await next(ctx);
                    }
                )
                : connectionBuilder;

        protected static ConnectionDelegate InitializeClientOpenSslDelegate
        (
            IServiceProvider serviceProvider,
            SslProtocol tlsProtocol,
            string cipher,
            bool awaitTask = false
        )
           => SetAwaitTask
            (
               new ConnectionBuilder(serviceProvider)
                .UseClientTls
                (
                   new SslOptions(tlsProtocol)
                   {
                       Ciphers = new string[] { cipher },
                       Pool = _Pool
                   }
                ),
               awaitTask
            )
           .Build();

        protected static ConnectionDelegate InitializeServerOpenSslDelegate
        (
            IServiceProvider serviceProvider,
            PrivateKey openSslKey,
            OpenSSL.Core.X509.X509Certificate openSslCertificate,
            SslProtocol tlsProtocol,
            bool awaitTask = false
        )
            => SetAwaitTask
            (
                new ConnectionBuilder(serviceProvider)
                .UseServerTls
                (
                    new SslOptions(openSslCertificate, openSslKey, tlsProtocol)
                    {
                        Pool = _Pool
                    }
                ),
                awaitTask
            )
            .Build();

        protected static ConnectionDelegate InitializeClientLegasySslDelegate
        (
            IServiceProvider serviceProvider,
            SslProtocols tlsProtocol,
            bool awaitTask = false
        )
        {
            TlsOptions options = new TlsOptions()
            {
                SslProtocols = tlsProtocol
            };

            options.AllowAnyRemoteCertificate();

            return SetAwaitTask
            (
                new ConnectionBuilder(serviceProvider)
                    .UseClientLegacySsl(options),
                awaitTask
            )
            .Build();
        }

        protected static ConnectionDelegate InitializeServerLegasySslDelegate
        (
            IServiceProvider serviceProvider,
            X509Certificate2 certificate,
            SslProtocols tlsProtocol,
            bool awaitTask = false
        )
        {
            TlsOptions options = new TlsOptions()
            {
                SslProtocols = tlsProtocol,
                LocalCertificate = certificate
            };

            return SetAwaitTask
            (
                new ConnectionBuilder(serviceProvider)
                    .UseServerLegacySsl(options),
                awaitTask
            )
            .Build();
        }
    }
}
