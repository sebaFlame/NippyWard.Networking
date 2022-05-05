using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public class NamedPipeConnectionFactory : IConnectionFactory
    {
        private IFeatureCollection _featureCollection;

        internal NamedPipeConnectionFactory()
        { }

        public NamedPipeConnectionFactory(IFeatureCollection featureCollection)
        {
            this._featureCollection = featureCollection;
        }

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

            return NamedPipeConnectionContext.ConnectAsync(namedPipeEndPoint, featureCollection: this._featureCollection);
        }
    }
}

