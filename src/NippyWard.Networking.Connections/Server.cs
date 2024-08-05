using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Connections
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

        private readonly ServerContext _serverContext;
        private readonly CancellationTokenSource _cts;
        private Task? _listenTask;
        private readonly ConnectionDelegate _connectionDelegate;
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<ulong, Task> _connections;
        private readonly IDictionary<EndPoint, IConnectionListener> _servers;

        public Server
        (
            ServerContext serverContext,
            ConnectionDelegate connectionDelegate,
            ILogger? logger
        )
        {
            this._serverContext = serverContext;
            this._connectionDelegate = connectionDelegate;
            this._logger = logger;

            this._cts = new CancellationTokenSource();
            this.ServerShutdown = this._cts.Token;
            this._connections = new ConcurrentDictionary<ulong, Task>();
            this._servers = new Dictionary<EndPoint, IConnectionListener>();
        }

        ~Server()
        {
            this._logger?.TraceLog("Server", "finalizer");

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
        /// Bind the server to all configured <see cref="IConnectionListenerFactory"/>
        /// This should only be called if you're unsure what the server will
        /// bind to, e.g. when using a proxy. This way you can call
        /// <see cref="TryGetConnectionListener(EndPoint, out IConnectionListener?)"/>
        /// to figure out what <see cref="EndPoint"/> it bound to (
        /// <see cref="IConnectionListener.EndPoint"/>).
        /// </summary>
        /// <param name="cancellationToken">To cancel the binding process</param>
        /// <returns>The binding Task</returns>
        public async Task BindAsync(CancellationToken cancellationToken = default)
        {
            IConnectionListenerFactory connectionListenerFactory;
            IConnectionListener connectionListener;

            foreach (KeyValuePair<EndPoint, IConnectionListenerFactory> kv in this._serverContext.Bindings)
            {
                if(this._servers.ContainsKey(kv.Key))
                {
                    continue;
                }

                connectionListenerFactory = kv.Value;

                try
                {
                    connectionListener = await connectionListenerFactory.BindAsync(kv.Key, cancellationToken);
                    this._servers.Add(kv.Key, connectionListener);
                }
                catch(Exception ex)
                {
                    this._logger?.LogError(ex, "[Server] Exception during binding");

                    this.Shutdown();

                    throw;
                }
            }
        }

        /// <summary>
        /// Start the server and return the execution.
        /// If the server has alrady started through <see cref="StartAsync(CancellationToken)"/>,
        /// one can await completion here.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to Cancel the listening.</param>
        /// <returns>A task representing the <see cref="Server"/> listening</returns>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            //if already started (through startasync)
            //make run awaitable
            if (this._listenTask is not null)
            {
                await this._listenTask;
                return;
            }

            Task[] listenTasks = new Task[this._serverContext.Bindings.Count];
            byte index = 0;

            //can throw exceptions
            await this.BindAsync(cancellationToken);

            foreach (KeyValuePair<EndPoint, IConnectionListener> kv in this._servers)
            {
                listenTasks[index] = this.ListenConnectionsAsync
                (
                    index,
                    this._serverContext.MaxClients,
                    kv.Value,
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
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if(this._listenTask is null)
            {
                return Task.CompletedTask;
            }

            this.Shutdown(this._serverContext.TimeOut);

            return this._listenTask;
        }

        public bool TryGetConnectionListener(EndPoint endpoint, [NotNullWhen(true)] out IConnectionListener? connectionListener)
        {
            if(!this._servers.ContainsKey(endpoint))
            {
                connectionListener = null;
                return false;
            }

            connectionListener = this._servers[endpoint];
            return true;
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
            ValueTask<ConnectionContext?> connectionTask;
            ConnectionContext? connectionContext;
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

                    //shutdown of listener
                    if(connectionContext is null)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    this._logger?.TraceLog("Server", "accepted connections");

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
                this._logger?.TraceLog("Server", "awaiting single connection");

                await Task.WhenAll(connections.Values);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken != cancellationToken)
                {
                    throw;
                }

                //shutdown has been called, await clients (if any)
                this._logger?.TraceLog("Server", "awaiting connections after shutdown");
                await this.Connections;
            }
            catch (Exception ex)
            {
                this._logger?.LogError(ex, "[Server] Unexpected exception from server");
                throw;
            }
            finally
            {
                this._logger?.TraceLog("Server", $"unbinding listener {listenerIndex}");
                await connectionListener.UnbindAsync(cancellationToken);
            }

            return connectionCount;
        }

        private static async Task ExecuteConnectionContext
        (
            ulong connectionId,
            ConnectionContext connectionContext,
            ConnectionDelegate connectionDelegate,
            ILogger? logger,
            IDictionary<ulong, Task> connections,
            CancellationToken cancellationToken
        )
        {
            CancellationTokenRegistration reg = default;

            //ensure no exception from the delegate gets thrown before any other awaitable
            //ensures add to connections collection
            await Task.Yield();

            reg = cancellationToken.Register((c) => ConnectionContextShutdown((ConnectionContext?)c!), connectionContext);

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
                logger?.LogError(ex, "Unexpected exception from connection {ConnectionId}", connectionContext.ConnectionId);
                throw;
            }
            finally
            {
                logger?.TraceLog($"{connectionContext.ConnectionId}", "disposing client");

                reg.Dispose();

                await connectionContext.DisposeAsync();

                connections.Remove(connectionId);
            }
        }

        private static void ConnectionContextShutdown(ConnectionContext connectionContext, bool lifetimeOnly = false)
        {
            IConnectionLifetimeNotificationFeature? connectionLifetimeNotificationFeature =
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
                this._cts.CancelAfter((int)timeoutInSeconds * 1000);
            }
            else
            {
                this._cts.Cancel();
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool _)
        {
            this.Shutdown();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                return new ValueTask(this.StopAsync());
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }   
    }
}
