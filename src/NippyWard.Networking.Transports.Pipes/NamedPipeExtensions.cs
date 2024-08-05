using System;
using System.Runtime.Versioning;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using NippyWard.Networking.Connections;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Transports.Pipes
{
    //only support Windows, because the Linux implementation is unix domain
    //sockets based, for which UseSocket is a better fit.
    public static class NamedPipeExtensions
    {
        private static ClientBuilder UseClientNamedPipe
        (
            this ClientBuilder clientBuilder,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<NamedPipeConnectionFactory>();

            IConnectionFactory connectionFactory = new NamedPipeConnectionFactory
            (
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return clientBuilder
                .AddBinding<NamedPipeEndPoint>(connectionFactory);
        }

        public static ClientBuilder UseNamedPipe(this ClientBuilder clientBuilder)
            => UseClientNamedPipe(clientBuilder);

        public static ClientBuilder UseNamedPipe(this ClientBuilder clientBuilder, Func<string> createName)
            => UseClientNamedPipe(clientBuilder, createName: createName);

        public static ClientBuilder UseNamedPipe
        (
            this ClientBuilder clientBuilder,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseClientNamedPipe(clientBuilder, createName, sendOptions, receiveOptions);

        private static ServerBuilder UseServerNamedPipe
        (
            ServerBuilder serverBuilder,
            NamedPipeEndPoint endpoint,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<NamedPipeConnectionFactory>();

            IConnectionListenerFactory connectionListenerFactory = new NamedPipeConnectionListenerFactory
            (
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return serverBuilder
                .AddBinding(endpoint, connectionListenerFactory);
        }

        public static ServerBuilder UseNamedPipe(this ServerBuilder serverBuilder, NamedPipeEndPoint endpoint)
            => UseServerNamedPipe(serverBuilder, endpoint);

        public static ServerBuilder UseNamedPipe(this ServerBuilder serverBuilder, NamedPipeEndPoint endpoint, Func<string> createName)
            => UseServerNamedPipe(serverBuilder, endpoint, createName: createName);

        public static ServerBuilder UseNamedPipe
        (
            this ServerBuilder serverBuilder,
            NamedPipeEndPoint endpoint,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseServerNamedPipe(serverBuilder, endpoint, createName, sendOptions, receiveOptions);
    }
}
