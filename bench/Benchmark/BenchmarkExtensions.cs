using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using ThePlague.Networking.Connections;
using Benchmark.LegacySsl;
using ThePlague.Networking.Logging;

namespace Benchmark
{
    internal static class BenchmarkExtensions
    {
        public static ClientBuilder UseStream(this ClientBuilder clientBuilder, Func<string> createConnectionId)
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<StreamConnectionContextFactory>();

            IConnectionFactory connectionFactory = new StreamConnectionContextFactory(createConnectionId: createConnectionId);

            return clientBuilder
                .AddBinding<IPEndPoint>(connectionFactory)
                .AddBinding<UnixDomainSocketEndPoint>(connectionFactory);
        }

        public static ServerBuilder UseStream(this ServerBuilder serverBuilder, EndPoint endPoint, Func<string> createConnectionId)
        {
            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<StreamConnectionContextListenerFactory>();

            IConnectionListenerFactory connectionListenerFactory = new StreamConnectionContextListenerFactory(createConnectionId: createConnectionId);

            return serverBuilder
                .AddBinding(endPoint, connectionListenerFactory);
        }

        public static IConnectionBuilder UseClientLegacySsl(this IConnectionBuilder connectionBuilder, TlsOptions options)
        {
            ILoggerFactory logger = connectionBuilder.ApplicationServices.GetService<ILoggerFactory>();
            return connectionBuilder.Use(next => new TlsClientConnectionMiddleware(next, options, logger).OnConnectionAsync);
        }

        public static IConnectionBuilder UseServerLegacySsl(this IConnectionBuilder connectionBuilder, TlsOptions options)
        {
            ILoggerFactory logger = connectionBuilder.ApplicationServices.GetService<ILoggerFactory>();
            return connectionBuilder.Use(next => new TlsServerConnectionMiddleware(next, options, logger).OnConnectionAsync);
        }
    }
}
