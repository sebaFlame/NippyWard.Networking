using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections.Middleware
{
    public static class ConnectionBuilderExtensions
    {
        /// <summary>
        /// Use a message parser and dispatcher as terminal middleware
        /// </summary>
        public static IConnectionBuilder UseProtocol<TMessage>
        (
            this IConnectionBuilder connectionBuilder,
            IMessageReader<TMessage> messageReader,
            IMessageWriter<TMessage> messageWriter,
            IMessageDispatcher<TMessage> messageDispatcher
        )
        {
            return connectionBuilder.Use
            (
                next => new Protocol<TMessage>
                (
                    messageReader,
                    messageWriter,
                    messageDispatcher
                ).OnConnectionAsync
            );
        }
    }
}
