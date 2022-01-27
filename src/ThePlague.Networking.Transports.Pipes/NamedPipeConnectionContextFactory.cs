using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public class NamedPipeConnectionFactory : IConnectionFactory
    {
        public ValueTask<ConnectionContext> ConnectAsync
        (
            EndPoint endpoint,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            if(endpoint is not NamedPipeEndPoint namedPipeEndPoint)
            {
                throw new NotSupportedException
                (
                    $"{endpoint.GetType().Name} not supported"
                );
            }

            return NamedPipeConnectionContext.ConnectAsync(namedPipeEndPoint);
        }
    }
}

