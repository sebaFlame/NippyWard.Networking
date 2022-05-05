using System;
using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using ThePlague.Networking.Connections;

#nullable enable

namespace ThePlague.Networking.Transports.Sockets
{
    public static class SocketExtensions
    {
        public static ClientBuilder UseSocket(this ClientBuilder clientBuilder)
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<SocketConnectionContextFactory>();

            IConnectionFactory connectionFactory = new SocketConnectionContextFactory(logger);

            return clientBuilder
                .AddBinding<IPEndPoint>(connectionFactory)
                .AddBinding<UnixDomainSocketEndPoint>(connectionFactory);
        }

        public static ClientBuilder UseSocket(this ClientBuilder clientBuilder, Func<string> createName)
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<SocketConnectionContextFactory>();

            IConnectionFactory connectionFactory = new SocketConnectionContextFactory(createName, logger);

            return clientBuilder
                .AddBinding<IPEndPoint>(connectionFactory)
                .AddBinding<UnixDomainSocketEndPoint>(connectionFactory);
        }

        public static ServerBuilder UseSocket(this ServerBuilder serverBuilder, EndPoint endpoint)
        {
            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<SocketConnectionContextListenerFactory>();

            IConnectionListenerFactory connectionListenerFactory = new SocketConnectionContextListenerFactory(logger);

            return serverBuilder
                .AddBinding(endpoint, connectionListenerFactory);
        }
    }
}
