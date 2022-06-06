using System;
using System.Net;
using System.Net.Sockets;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Transports.Pipes;

namespace ThePlague.Networking.Tests
{
    internal static class EndPointExtensions
    {
        internal static ServerBuilder ConfigureEndpoint
        (
            this ServerBuilder serverBuilder,
            EndPoint endpoint,
            Func<string> createName
        )
        {
            return endpoint switch
            {
                IPEndPoint
                    => serverBuilder.UseSocket(endpoint, createName),
                UnixDomainSocketEndPoint
                    => serverBuilder.UseSocket(endpoint, createName),
                NamedPipeEndPoint
                    => serverBuilder.UseNamedPipe((NamedPipeEndPoint)endpoint, createName),
                _
                    => throw new NotSupportedException()
            };
        }

        internal static ClientBuilder ConfigureEndpoint
        (
            this ClientBuilder clientBuilder,
            EndPoint endpoint,
            Func<string> createName
        )
        {
            return endpoint switch
            {
                IPEndPoint
                    => clientBuilder.UseSocket(createName),
                UnixDomainSocketEndPoint
                    => clientBuilder.UseSocket(createName),
                NamedPipeEndPoint
                    => clientBuilder.UseNamedPipe(createName),
                _
                    => throw new NotSupportedException()
            };
        }
    }
}
