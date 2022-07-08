using System;
using System.Net;
using System.Net.Sockets;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Connections;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Transports.Proxy
{
    public static class ProxyExtensions
    {
        private static ClientBuilder UseClientProxy
        (
            ClientBuilder clientBuilder,
            Uri proxyUri,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<ProxyConnectionFactory>();

            IConnectionFactory connectionFactory = new ProxyConnectionFactory
            (
                proxyUri: proxyUri,
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return clientBuilder
                .AddBinding<IPEndPoint>(connectionFactory);
        }

        public static ClientBuilder UseProxy(this ClientBuilder clientBuilder, Uri proxyUri)
            => UseClientProxy(clientBuilder, proxyUri);

        public static ClientBuilder UseProxy(this ClientBuilder clientBuilder, Uri proxyUri, Func<string> createName)
            => UseClientProxy(clientBuilder, proxyUri, createName);

        public static ClientBuilder UseProxy
        (
            this ClientBuilder clientBuilder,
            Uri proxyUri,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseClientProxy(clientBuilder, proxyUri, createName, sendOptions, receiveOptions);

        private static ServerBuilder UseServerProxy
        (
            ServerBuilder serverBuilder,
            Uri proxyUri,
            IPEndPoint proxyEndPoint,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<ProxyConnectionListenerFactory>();

            IConnectionListenerFactory connectionListenerFactory = new ProxyConnectionListenerFactory
            (
                proxyUri: proxyUri,
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return serverBuilder
                .AddBinding(proxyEndPoint, connectionListenerFactory);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="serverBuilder"></param>
        /// <param name="proxyUri"></param>
        /// <param name="proxyEndPoint">This is just an indication. You need to check <see cref="IConnectionListener.EndPoint"/> from
        ///  <see cref="Server.TryGetConnectionListener(EndPoint, out IConnectionListener?)"/> for the correct endpoint</param>
        /// <returns></returns>
        public static ServerBuilder UseProxy(this ServerBuilder serverBuilder, Uri proxyUri, IPEndPoint proxyEndPoint)
            => UseServerProxy(serverBuilder, proxyUri, proxyEndPoint);

        public static ServerBuilder UseProxy(this ServerBuilder serverBuilder, Uri proxyUri, IPEndPoint proxyEndPoint, Func<string> createName)
            => UseServerProxy(serverBuilder, proxyUri, proxyEndPoint, createName);

        public static ServerBuilder UseProxy
        (
            this ServerBuilder serverBuilder,
            Uri proxyUri,
            IPEndPoint proxyEndPoint,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseServerProxy(serverBuilder, proxyUri, proxyEndPoint, createName, sendOptions, receiveOptions);
    }
}
