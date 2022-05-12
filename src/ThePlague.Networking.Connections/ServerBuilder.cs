using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;

#nullable enable

namespace ThePlague.Networking.Connections
{
    public class ServerBuilder
    {
        public IServiceProvider ApplicationServices => this._applicationServices;

        private IConnectionBuilder _connectionBuilder;
        private readonly IServiceProvider _applicationServices;
        private readonly ServerContext _serverContext;

        public ServerBuilder()
            : this(EmptyServiceProvider.Instance)
        { }

        public ServerBuilder(IServiceProvider serviceProvider)
        {
            this._applicationServices = serviceProvider;
            this._connectionBuilder = new ConnectionBuilder(serviceProvider);
            this._serverContext = new ServerContext();
        }

        private ConnectionDelegate BuildConnectionDelegate()
        {
            return this._connectionBuilder.Build();
        }

        public ServerBuilder ConfigureConnection(Func<IConnectionBuilder, IConnectionBuilder> configureConnectionbuilder)
        {
            this._connectionBuilder = configureConnectionbuilder(this._connectionBuilder);
            return this;
        }

        /// <summary>
        /// Configure the <see cref="Server"/> to only allow a single connection.
        /// And await that single connection during execution.
        /// </summary>
        public ServerBuilder ConfigureSingleConnection(bool isSingle = true)
        {
            this._serverContext.AcceptSingleConnection = isSingle;
            return this;
        }

        /// <summary>
        /// Add a binding on <see cref="EndPoint"/> <paramref name="endpoint"/> to start listening on.
        /// </summary>
        /// <param name="endpoint">The <see cref="EndPoint"/> to bind to</param>
        /// <param name="connectionListenerFactory">The <see cref="IConnectionListenerFactory"/> to create the binding</param>
        public ServerBuilder AddBinding(EndPoint endpoint, IConnectionListenerFactory connectionListenerFactory)
        {
            this._serverContext.Bindings.Add(endpoint, connectionListenerFactory);
            return this;
        }

        /// <summary>
        /// Seconds to wait for connections to close.
        /// Force close after this timeout.
        /// </summary>
        /// <param name="timeoutInSeconds">Timeout in seconds</param>
        public ServerBuilder AddTimeout(uint timeoutInSeconds)
        {
            this._serverContext.TimeOut = timeoutInSeconds;
            return this;
        }

        private Task BuildListenTask(CancellationToken shutdownCancellation = default)
            => this.BuildServer().RunAsync(shutdownCancellation);

        /// <summary>
        /// Builds the configured disposable server.
        /// </summary>
        /// <returns>A <see cref="Server"/> instance.</returns>
        public Server BuildServer()
        {
            ConnectionDelegate connectionDelegate = this.BuildConnectionDelegate();

            return new Server
            (
                this._serverContext,
                connectionDelegate,
                this._applicationServices.CreateLogger<Server>()
            );
        }

        public Task Build(CancellationToken shutdownCancellation = default)
            => this.BuildListenTask(shutdownCancellation);

        /// <summary>
        /// Builds a <see cref="Task"/> representing a server listening to multiple incoming connections per <see cref="ServerContext.Bindings"/>.
        /// </summary>
        /// <param name="shutdownCancellation">A <see cref="CancellationToken"/> by which the server can be shutdown, this is not optional!!!</param>
        /// <returns>A task representing the listening to multiple <see cref="ConnectionContext"/></returns>
        public Task BuildMultiClient(CancellationToken shutdownCancellation)
        {
            this.ConfigureSingleConnection(false);
            return this.Build(shutdownCancellation);
        }   

        /// <summary>
        /// Builds a <see cref="Task"/> representing a server listening to single incoming connection per <see cref="ServerContext.Bindings"/>.
        /// </summary>
        /// <param name="shutdownCancellation">An optional <see cref="CancellationToken"/> by which the server can be shutdown</param>
        /// <returns>A task representing the listening to single <see cref="ConnectionContext"/></returns>
        public Task BuildSingleClient(CancellationToken shutdownCancellation = default)
        {
            this.ConfigureSingleConnection(true);
            return this.Build(shutdownCancellation);
        }
    }
}
