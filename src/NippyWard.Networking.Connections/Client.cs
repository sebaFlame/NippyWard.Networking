using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Connections
{
    public class Client : IHostedService, IDisposable, IAsyncDisposable
    {
        private readonly ConnectionDelegate _connectionDelegate;
        private readonly ILogger? _logger;
        private readonly ConnectionContext _connectionContext;
        private Task? _clientTask;

        public Client
        (
            ConnectionContext connectionContext,
            ConnectionDelegate connectionDelegate,
            ILogger? logger
        )
        {
            this._connectionContext = connectionContext;
            this._connectionDelegate = connectionDelegate;
            this._logger = logger;
        }

        ~Client()
        {
            this._logger?.TraceLog(this._connectionContext.ConnectionId ,"client finalizer");

            this.Dispose(false);
        }

        /// <summary>
        /// Start the client and defer execution.
        /// Can be used in an <see cref="IHostedService"/>.
        /// </summary>
        /// <param name="cancellationToken">Not used</param>
        /// <returns>The start Task</returns>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            //do not pass a valid CancellationToken, lifetime is maintained by the Client object
            this._clientTask = this.RunAsync();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Start the client and return the execution.
        /// If the client has alrady started through <see cref="StartAsync(CancellationToken)"/>,
        /// one can await completion here.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to Cancel the exceution.</param>
        /// <returns>A task representing the <see cref="Client"/> execution</returns>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            //if already started (through startasync)
            //make run awaitable
            if (this._clientTask is not null)
            {
                await this._clientTask;
                return;
            }

            //register Cancellation, Client could be executed as a delegate
            CancellationTokenRegistration reg = cancellationToken.Register(c => ConnectionContextShutdown((ConnectionContext)c!), this._connectionContext);

            try
            {
                await this._connectionDelegate(this._connectionContext);
            }
            catch(Exception ex)
            {
                this._logger?.LogError(ex, "Unexpected exception from connection {ConnectionId}", this._connectionContext.ConnectionId);
                throw;
            }
            finally
            {
                this._logger?.TraceLog(this._connectionContext.ConnectionId, "disposing client");

                reg.Dispose();

                await this._connectionContext.DisposeAsync();
            }
        }

        /// <summary>
        /// Stop the client and await execution to finish.
        /// Can be used in an <see cref="IHostedService"/>.
        /// </summary>
        /// <param name="cancellationToken">Not used</param>
        /// <returns>The finishing execution</returns>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if(this._clientTask is null)
            {
                return Task.CompletedTask;
            }

            this.Shutdown();

            return this._clientTask;
        }

        private static void ConnectionContextShutdown(ConnectionContext connectionContext, bool lifetimeOnly = false)
        {
            if(connectionContext is null)
            {
                return;
            }

            IConnectionLifetimeNotificationFeature? connectionLifetimeNotificationFeature =
                connectionContext.Features.Get<IConnectionLifetimeNotificationFeature>();

            //prioritize "clean" shutdown
            if (connectionLifetimeNotificationFeature is not null)
            {
                connectionLifetimeNotificationFeature.RequestClose();

            }
            else if (!lifetimeOnly)
            {
                connectionContext.Abort();
            }
        }

        private void Shutdown()
        {
            ConnectionContextShutdown(this._connectionContext, true);
        }

        public void Dispose()
        {
            this.Dispose(true);
        }

        internal void Dispose(bool isDisposing)
        {
            try
            {
                ConnectionContextShutdown(this._connectionContext);
            }
            catch
            { }

            if(!isDisposing)
            {
                return;
            }

            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
            => new ValueTask(this.StopAsync());
    }
}
