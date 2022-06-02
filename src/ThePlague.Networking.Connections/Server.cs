using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThePlague.Networking.Logging;

namespace ThePlague.Networking.Connections
{
    public class Server : IHostedService, IServerLifetimeFeature, IDisposable, IAsyncDisposable
    {
        public CancellationToken ServerShutdown { get; set; }
        public Task Connections
        {
            get
            {
                if(this._connections.IsEmpty)
                {
                    return Task.CompletedTask;
                }

                return Task.WhenAll(this._connections.Values);
            }
        }

        private ServerContext _serverContext;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private ConnectionDelegate _connectionDelegate;
        private ILogger _logger;
        ConcurrentDictionary<ulong, Task> _connections;

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
            this._connections = new ConcurrentDictionary<ulong, Task>();
        }

        ~Server()
        {
            this._logger.TraceLog("Server", "finalizer");

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
            byte index = 0;

            foreach (KeyValuePair<EndPoint, IConnectionListenerFactory> kv in this._serverContext.Bindings)
            {
                connectionListenerFactory = kv.Value;
                connectionListener = await connectionListenerFactory.BindAsync(kv.Key, cancellationToken);

                listenTasks[index] = this.ListenConnectionsAsync
                (
                    index,
                    this._serverContext.MaxClients,
                    connectionListener,
                    this._connections,
                    cancellationToken
                );

                index++;
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
            this.Shutdown(this._serverContext.TimeOut);

            await this._listenTask;
        }

        //max 256 listeners!!!
        private async Task<ulong> ListenConnectionsAsync
        (
            byte listenerIndex,
            uint maxClients,
            IConnectionListener connectionListener,
            IDictionary<ulong, Task> connections,
            CancellationToken cancellationToken
        )
        {
            ValueTask<ConnectionContext> connectionTask;
            ConnectionContext connectionContext;
            ulong connectionCount = 0, connectionId;

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

                    this._logger.TraceLog("Server", "accepted connections");

                    connectionContext.Features.Set<IServerLifetimeFeature>(this);

                    //generate a connectionId
                    connectionId = ((ulong)listenerIndex) | (++connectionCount) << 8;

                    //execute client on threadpool
                    connections.Add
                    (
                        connectionId,
                        ExecuteConnectionContext
                        (
                            connectionId,
                            connectionContext,
                            this._connectionDelegate,
                            this._logger,
                            connections,
                            cancellationToken
                        )
                    );
                } while (maxClients == 0
                    || connectionCount < maxClients);

                //only single connection, await it
                this._logger.TraceLog("Server", "awaiting single connection");
                await this.Connections;
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken != cancellationToken)
                {
                    throw;
                }

                //shutdown has been called, await clients (if any)
                this._logger.TraceLog("Server", "awaiting connections after shutdown");
                await this.Connections;
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "[Server] Unexpected exception from server");
                throw;
            }
            finally
            {
                this._logger.TraceLog("Server", $"unbinding listener {listenerIndex}");
                await connectionListener.UnbindAsync(cancellationToken);
            }

            return connectionCount;
        }

        private static async Task ExecuteConnectionContext
        (
            ulong connectionId,
            ConnectionContext connectionContext,
            ConnectionDelegate connectionDelegate,
            ILogger logger,
            IDictionary<ulong, Task> connections,
            CancellationToken cancellationToken
        )
        {
            CancellationTokenRegistration reg = default;

            //ensure no exception from the delegate gets thrown before any other awaitable
            //ensures add to connections collection
            await Task.Yield();

            reg = cancellationToken.UnsafeRegister((c) => ConnectionContextShutdown((ConnectionContext)c), connectionContext);

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
                logger.TraceLog($"{connectionContext.ConnectionId}", "disposing client");

                reg.Dispose();

                await connectionContext.DisposeAsync();

                connections.Remove(connectionId);
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

        //TODO: try connectionLifetimeNotificationFeature.RequestClose() first
        public void Shutdown(uint timeoutInSeconds = 0)
        {
            if(timeoutInSeconds > 0)
            {
                this._cts?.CancelAfter((int)timeoutInSeconds * 1000);
            }
            else
            {
                this._cts?.Cancel();
            }
            
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
