using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Logging;

#nullable enable

namespace ThePlague.Networking.Connections
{
    public class ClientBuilder
    {
        public IServiceProvider ApplicationServices => this._applicationServices;

        private IConnectionBuilder _connectionBuilder;
        private readonly IServiceProvider _applicationServices;
        private readonly ClientContext _clientContext;

        public ClientBuilder()
            : this(EmptyServiceProvider.Instance)
        { }

        public ClientBuilder(IServiceProvider applicationServices)
        {
            this._applicationServices = applicationServices;
            this._connectionBuilder = new ConnectionBuilder(applicationServices);
            this._clientContext = new ClientContext();
        }

        private ConnectionDelegate BuildConnectionDelegate()
        {
            return this._connectionBuilder.Build();
        }

        public ClientBuilder ConfigureConnection(Func<IConnectionBuilder, IConnectionBuilder> configureConnectionbuilder)
        {
            this._connectionBuilder = configureConnectionbuilder(this._connectionBuilder);
            return this;
        }

        /// <summary>
        /// Add a <see cref="IConnectionFactory"/> for EndPoints of type <typeparamref name="T"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="connectionFactory"><see cref="IConnectionFactory"/> to create <see cref="ConnectionContext"/> for <typeparamref name="T"/></param>
        public ClientBuilder AddBinding<T>(IConnectionFactory connectionFactory)
            where T : EndPoint
        {
            this._clientContext.Bindings.Add(typeof(T), connectionFactory);
            return this;
        }

        /// <summary>
        /// Creates a factory to create multiple clients using the configured <see cref="ConnectionDelegate"/>
        /// </summary>
        /// <returns>A factory</returns>
        public ClientFactory BuildClientFactory()
        {
            ConnectionDelegate connectionDelegate = this.BuildConnectionDelegate();

            //ensure the first delegate provides an IConnectionFactory
            return new ClientFactory
            (
                connectionDelegate,
                this._clientContext,
                this._applicationServices.CreateLogger<ClientFactory>()
            );
        }

        /// <summary>
        /// Creates a single disposable,connected <see cref="Client"/> instance which can be used to track the connection.
        /// The <see cref="ConnectionDelegate"/> configured using <see cref="ConfigureConnection(Func{IConnectionBuilder, IConnectionBuilder})"/>
        /// can be started using <see cref="Client.StartAsync(CancellationToken)"/> or <see cref="Client.RunAsync(CancellationToken)"/>.
        /// </summary>
        /// <param name="endPoint">The <see cref="EndPoint"/> to connect to</param>
        /// <param name="cancellationToken">A cancellation token to cancel the connect to the <paramref name="endPoint"/></param>
        /// <returns>A connected client instance</returns>
        public Task<Client> BuildClient(EndPoint endPoint, CancellationToken cancellationToken = default)
            => this.BuildClientFactory()
                .ConnectAsync(endPoint, cancellationToken);

        /// <summary>
        /// This is a task representing a single connected <see cref="Client"/> and executing
        /// the <see cref="ConnectionDelegate"/> configured using <see cref="ConfigureConnection(Func{IConnectionBuilder, IConnectionBuilder})"/>
        /// The underlying <see cref="Client"/> object will get GC'd!
        /// </summary>
        /// <param name="endPoint">The <see cref="EndPoint"/> to connect to</param>
        /// <param name="shutdownCancellationToken">A <see cref="CancellationToken"/> which can be used to abort the client</param>
        /// <returns>A <see cref="Task"/> representing a running client</returns>
        public Task Build(EndPoint endPoint, CancellationToken shutdownCancellationToken = default)
            => this.BuildClientFactory()
                .RunClientAsync(endPoint, shutdownCancellationToken);
    }
}