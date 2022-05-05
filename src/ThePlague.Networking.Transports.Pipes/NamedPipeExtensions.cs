using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public static class NamedPipeExtensions
    {
        public static ClientBuilder UseNamedPipe(this ClientBuilder clientBuilder)
        {
            IConnectionFactory connectionFactory = new NamedPipeConnectionFactory();

            return clientBuilder
                .AddBinding<NamedPipeEndPoint>(connectionFactory);
        }

        public static ServerBuilder UseNamedPipe(this ServerBuilder serverBuilder, NamedPipeEndPoint endpoint)
        {
            IConnectionListenerFactory connectionListenerFactory = new NamedPipeConnectionListenerFactory();

            return serverBuilder
                .AddBinding(endpoint, connectionListenerFactory);
        }
    }
}
