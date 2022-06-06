using System;
using System.Net;
using System.Net.Sockets;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Logging;

#nullable enable

namespace ThePlague.Networking.Transports.Sockets
{
    public static class SocketExtensions
    {
        /// <summary>
        /// PipeOptions with <see cref="PipeOptions.ResumeWriterThreshold"/> and <see cref="PipeOptions.PauseWriterThreshold"/> set to 1.
        /// This guarantees a <see cref="PipeWriter.FlushAsync(System.Threading.CancellationToken)"/> always completely
        /// flushes the <see cref="PipeReader.ReadAsync(System.Threading.CancellationToken)"/>.
        /// </summary>
        public static PipeOptions _BlockingOptions;

        static SocketExtensions()
        {
            _BlockingOptions = new PipeOptions
            (
                resumeWriterThreshold: 1,
                pauseWriterThreshold: 1
            );
        }

        private static ClientBuilder UseClientSocket
        (
            ClientBuilder clientBuilder,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<SocketConnectionContextFactory>();

            IConnectionFactory connectionFactory = new SocketConnectionContextFactory
            (
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return clientBuilder
                .AddBinding<IPEndPoint>(connectionFactory)
                .AddBinding<UnixDomainSocketEndPoint>(connectionFactory);
        }

        public static ClientBuilder UseSocket(this ClientBuilder clientBuilder)
            => UseClientSocket(clientBuilder);

        public static ClientBuilder UseSocket(this ClientBuilder clientBuilder, Func<string> createName)
            => UseClientSocket(clientBuilder, createName);

        public static ClientBuilder UseSocket
        (
            this ClientBuilder clientBuilder,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseClientSocket(clientBuilder, createName, sendOptions, receiveOptions);

        /// <summary>
        /// Creates a client with an <see cref="IDuplexPipe.Output"/> which waits (asynchronously) on all bytes being flushed to socket.
        /// Use this with care as this removes most of the advantages of using a <see cref="Pipe"/>. Instead use <see cref="PipeWriter.CompleteAsync(Exception?)"/>
        /// to await send thread completion.
        /// </summary>
        public static ClientBuilder UseBlockingSendSocket(this ClientBuilder clientBuilder, Func<string> createName)
            => UseClientSocket(clientBuilder, createName, sendOptions: _BlockingOptions);

        private static ServerBuilder UseServerSocket
        (
            ServerBuilder serverBuilder,
            EndPoint endpoint,
            Func<string>? createName = null,
            PipeOptions? sendOptions = null,
            PipeOptions? receiveOptions = null
        )
        {
            //weird behavoir using UnixDomainSocketEndPoint in Windows
            //sometimes can not do graceful shutdown
            if (endpoint is UnixDomainSocketEndPoint
                && !OperatingSystem.IsLinux())
            {
                throw new NotSupportedException($"{nameof(UnixDomainSocketEndPoint)} not supported on {Environment.OSVersion}");
            }

            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<SocketConnectionContextListenerFactory>();

            IConnectionListenerFactory connectionListenerFactory = new SocketConnectionContextListenerFactory
            (
                createName: createName,
                sendOptions: sendOptions,
                receiveOptions: receiveOptions,
                logger: logger
            );

            return serverBuilder
                .AddBinding(endpoint, connectionListenerFactory);
        }

        public static ServerBuilder UseSocket(this ServerBuilder serverBuilder, EndPoint endpoint)
            => UseServerSocket(serverBuilder, endpoint);

        public static ServerBuilder UseSocket(this ServerBuilder serverBuilder, EndPoint endpoint, Func<string> createName)
            => UseServerSocket(serverBuilder, endpoint, createName);

        public static ServerBuilder UseSocket
        (
            this ServerBuilder serverBuilder,
            EndPoint endpoint,
            Func<string> createName,
            PipeOptions sendOptions,
            PipeOptions receiveOptions
        )
            => UseServerSocket(serverBuilder, endpoint, createName, sendOptions, receiveOptions);

        /// <summary>
        /// Server crates clients with an <see cref="IDuplexPipe.Output"/> which waits (asynchronously) on all bytes being flushed to socket.
        /// Use this with care as this removes most of the advantages of using a <see cref="Pipe"/>. Instead use <see cref="PipeWriter.CompleteAsync(Exception?)"/>
        /// to await send thread completion.
        /// </summary>
        public static ServerBuilder UseBlockingSendSocket(this ServerBuilder serverBuilder, EndPoint endpoint, Func<string> createName)
            => UseServerSocket(serverBuilder, endpoint, createName, sendOptions: _BlockingOptions);
    }
}
