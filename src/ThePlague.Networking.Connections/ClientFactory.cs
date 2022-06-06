using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Connections
{
    public class ClientFactory
    {
        private ConnectionDelegate _connectionDelegate;
        private ClientContext _clientContext;
        private ILogger? _logger;

        public ClientFactory 
        (
            ConnectionDelegate connectionDelegate,
            ClientContext clientContext,
            ILogger? logger
        )
        {
            this._connectionDelegate = connectionDelegate;
            this._clientContext = clientContext;
            this._logger = logger;
        }

        private IConnectionFactory GetConnectionFactory(EndPoint endPoint)
        {
            return this._clientContext.Bindings[endPoint.GetType()];
        }

        private ValueTask<ConnectionContext> CreateConnectionContext(EndPoint endPoint, CancellationToken cancellationToken)
        {
            IConnectionFactory connectionFactory = this.GetConnectionFactory(endPoint);
            return connectionFactory.ConnectAsync(endPoint, cancellationToken);
        }

        /// <summary>
        /// Creates a single disposable,connected <see cref="Client"/> instance which can be used to track the connection.
        /// The excution can be started using <see cref="Client.StartAsync(CancellationToken)"/> or <see cref="Client.RunAsync(CancellationToken)"/>.
        /// </summary>
        /// <returns>A connected client</returns>
        public async Task<Client> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            ConnectionContext connectionContext = await this.CreateConnectionContext(endPoint, cancellationToken);
            return new Client(connectionContext, this._connectionDelegate, this._logger);
        }

        /// <summary>
        /// This is a task representing a single connected <see cref="Client"/> processing execution.
        /// </summary>
        /// <param name="endPoint">The <see cref="EndPoint"/> to connect to</param>
        /// <param name="shutdownCancellationToken">A <see cref="CancellationToken"/> which can be used to abort the task</param>
        /// <returns>A <see cref="Task"/> representing a running client</returns>
        public async Task RunClientAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        {
            Client client = await this.ConnectAsync(endPoint, cancellationToken);
            await client.RunAsync(cancellationToken);
        }
    }
}
