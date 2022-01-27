using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public class NamedPipeConnectionListenerFactory : IConnectionListenerFactory
    {
        public ValueTask<IConnectionListener> BindAsync
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

            NamedPipeServer server = new NamedPipeServer(namedPipeEndPoint);

            server.Bind();

            return ValueTask.FromResult<IConnectionListener>(server);
        }
    }
}