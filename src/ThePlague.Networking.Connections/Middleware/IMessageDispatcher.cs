using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections.Middleware
{
    public interface IMessageDispatcher<TMessage>
    {
        /// <summary>
        /// A connection has been made using <paramref name="connectionContext"/>.
        /// </summary>
        /// <param name="connectionContext">The connection context</param>
        public ValueTask OnConnectedAsync
        (
            ConnectionContext connectionContext,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// Dispatch a parsed<paramref name="message"/> from <paramref name="connectionContext"/>
        /// for processing.
        /// </summary>
        /// <param name="connectionContext">The connection context which received the message</param>
        /// <param name="message">A parsed message</param>
        public ValueTask DispatchMessageAsync
        (
            ConnectionContext connectionContext,
            TMessage message,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// A connection using <paramref name="connectionContext"/> has been shut down.
        /// </summary>
        /// <param name="connectionContext">The connection context</param>
        /// <param name="ex">Optional <see cref="Exception"/> thrown during the connection.</param>
        public ValueTask OnDisconnectedAsync
        (
            ConnectionContext connectionContext,
            Exception? ex = null,
            CancellationToken cancellationToken = default
        );
    }
}