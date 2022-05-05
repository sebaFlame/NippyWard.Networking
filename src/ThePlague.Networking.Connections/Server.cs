using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Connections
{
    public class Server : IHostedService, IServerLifetimeFeature, IDisposable, IAsyncDisposable
    {
        public CancellationToken ServerShutdown { get; set; }

        private ServerContext _serverContext;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private ConnectionDelegate _connectionDelegate;
        private ILogger _logger;

        public Server
        (
            ServerContext serverContext,
            ConnectionDelegate connectionDelegate,
            ILogger logger
        )
        {
            this._serverContext = serverContext;
            this._connectionDelegate = connectionDelegate;
            this._logger = logger;

            this._cts = new CancellationTokenSource();
            this.ServerShutdown = this._cts.Token;
        }

        ~Server()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Start the server and defer execution.
        /// Can be used in an <see cref="IHostedService"/>.
        /// </summary>
        /// <param name="cancellationToken">Not used</param>
        /// <returns>The start Task</returns>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            this._listenTask = this.RunAsync(this._cts.Token);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Start the server and return the execution.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to Cancel the listening.</param>
        /// <returns>A task representing the <see cref="Server"/> listening</returns>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            IConnectionListenerFactory connectionListenerFactory;
            IConnectionListener connectionListener;

            Task[] listenTasks = new Task[this._serverContext.Bindings.Count];
            int index = 0;

            foreach (KeyValuePair<EndPoint, IConnectionListenerFactory> kv in this._serverContext.Bindings)
            {
                connectionListenerFactory = kv.Value;
                connectionListener = await connectionListenerFactory.BindAsync(kv.Key, cancellationToken);

                if(this._serverContext.AcceptSingleConnection)
                {
                    listenTasks[index++] = this.ListenSingleConnectionAsync(connectionListener, cancellationToken);
                }
                else
                {
                    listenTasks[index++] = this.ListenMultipleConnectionAsync(connectionListener, cancellationToken);
                }
            }

            await Task.WhenAll(listenTasks);
        }

        /// <summary>
        /// Stop the server and await execution to finish.
        /// Can be used in an <see cref="IHostedService"/>.
        /// </summary>
        /// <param name="cancellationToken">Not used</param>
        /// <returns>The finishing execution</returns>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            this.Shutdown();

            await this._listenTask;
        }

        private async Task ListenConnectionsAsync
        (
            bool listenMultiple,
            IConnectionListener connectionListener,
            CancellationToken cancellationToken
        )
        {
            ValueTask<ConnectionContext> connectionTask;
            ConnectionContext connectionContext;
            Task executionTask;

            try
            {
                do
                {
                    connectionTask = connectionListener.AcceptAsync(cancellationToken);

                    if (!connectionTask.IsCompletedSuccessfully)
                    {
                        connectionContext = await connectionTask;
                    }
                    else
                    {
                        connectionContext = connectionTask.Result;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    connectionContext.Features.Set<IServerLifetimeFeature>(this);

                    //execute client on threadpool
                    executionTask = ExecuteConnectionContext
                    (
                        connectionContext,
                        this._connectionDelegate,
                        this._logger,
                        cancellationToken
                    );
                } while (listenMultiple);

                if (!listenMultiple)
                {
                    await executionTask;
                }
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken != cancellationToken)
                {
                    throw;
                }
            }
            finally
            {
                await connectionListener.UnbindAsync(cancellationToken);
            }
        }

        internal Task ListenMultipleConnectionAsync
        (
            IConnectionListener connectionListener,
            CancellationToken cancellationToken
        )
            => this.ListenConnectionsAsync(true, connectionListener, cancellationToken);

        internal Task ListenSingleConnectionAsync
        (
            IConnectionListener connectionListener,
            CancellationToken cancellationToken
        )
            => this.ListenConnectionsAsync(false, connectionListener, cancellationToken);

        private static async Task ExecuteConnectionContext
        (
            ConnectionContext connectionContext,
            ConnectionDelegate connectionDelegate,
            ILogger logger,
            CancellationToken cancellationToken
        )
        {
            CancellationTokenRegistration reg = default;

            //ensure no exception from the delegate gets thrown before any other awaitable
            await Task.Yield();

            reg = cancellationToken.Register((c) => ConnectionContextShutdown((ConnectionContext)c), connectionContext, false);

            try
            {
                await connectionDelegate(connectionContext);
            }
            catch(OperationCanceledException ex)
            {
                //not canceled on shutdown
                if(ex.CancellationToken != cancellationToken)
                {
                    throw;
                }
            }
            catch(Exception ex)
            {
                logger.LogError(ex, "Unexpected exception from connection {ConnectionId}", connectionContext.ConnectionId);
                throw;
            }
            finally
            {
                reg.Dispose();

                await connectionContext.DisposeAsync();
            }
        }

        private static void ConnectionContextShutdown(ConnectionContext connectionContext, bool lifetimeOnly = false)
        {
            IConnectionLifetimeNotificationFeature connectionLifetimeNotificationFeature =
                connectionContext.Features.Get<IConnectionLifetimeNotificationFeature>();

            //prioritize "clean" shutdown
            if (connectionLifetimeNotificationFeature is not null)
            {
                connectionLifetimeNotificationFeature.RequestClose();
                
            }
            else if(!lifetimeOnly)
            {
                connectionContext.Abort();
            }
        }

        public void Shutdown()
        {
            this._cts?.Cancel();
            this._cts = null;
        }

        public void Dispose()
        {
            this.Dispose(true);
        }

        internal void Dispose(bool isDisposing)
        {
            this.Shutdown();

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
