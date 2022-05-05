using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    public class NamedPipeConnectionListenerFactory : IConnectionListenerFactory
    {
        private IFeatureCollection _featureCollection;

        internal NamedPipeConnectionListenerFactory()
        { }

        public NamedPipeConnectionListenerFactory(IFeatureCollection featureCollection)
        {
            this._featureCollection = featureCollection;
        }

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

            NamedPipeServer server = new NamedPipeServer(namedPipeEndPoint, this._featureCollection);

            server.Bind();

            return ValueTask.FromResult<IConnectionListener>(server);
        }
    }
}