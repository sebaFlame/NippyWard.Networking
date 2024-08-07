﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Connections.Middleware
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
            where TMessage : class
        {
            ILogger? logger = connectionBuilder.ApplicationServices.CreateLogger<Protocol<TMessage>>();

            return connectionBuilder.Use
            (
                next => new Protocol<TMessage>
                (
                    messageReader,
                    messageWriter,
                    messageDispatcher,
                    logger
                ).OnConnectionAsync
            );
        }
    }
}
