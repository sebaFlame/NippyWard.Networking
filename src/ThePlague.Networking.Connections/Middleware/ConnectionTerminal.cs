using System;
using System.Threading.Tasks;
using System.IO.Pipelines;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

using ThePlague.Networking.Pipelines;
using System.Threading;

namespace ThePlague.Networking.Connections.Middleware
{
    internal class ConnectionTerminal
    {
        //no next delegate allowed, terminal!
        public ConnectionTerminal()
        { }

        internal async Task OnConnectionAsync
        (
            ConnectionContext connectionContext
        )
        {
            IDuplexPipe originalTransport = connectionContext.Transport;

            //ensure these get scoped per ConnectionContext
            TaskCompletionSource tcs = new TaskCompletionSource();
            CancellationTokenSource connectionClosedRequestedTokenSource = new CancellationTokenSource();
            CancellationTokenRegistration? reg = null;

            //override pipes to register completion to _taskCompletion
            IDuplexPipe newTransport = new DuplexPipe
            (
                new TerminalPipeReader(originalTransport, tcs),
                new TerminalPipeWriter(originalTransport, tcs)
            );

            connectionContext.Transport = newTransport;

            //register abort to _taskCompletion
            IConnectionLifetimeFeature connectionLifetimeFeature = connectionContext.Features.Get<IConnectionLifetimeFeature>();
            if(connectionLifetimeFeature is not null)
            {
                reg = connectionLifetimeFeature
                    .ConnectionClosed
                    .Register((t) => ((TaskCompletionSource)t).SetResult(), tcs, false);
            }

            connectionContext.Features.Set<IConnectionLifetimeNotificationFeature>
            (
                new TerminalConnectionLifetime
                (
                    tcs,
                    connectionClosedRequestedTokenSource
                )
            );

            //it needs to block on the terminal
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                reg?.Dispose();

                connectionClosedRequestedTokenSource.Dispose();

                //restore original transport
                connectionContext.Transport = originalTransport;
            }
        }

        private class TerminalConnectionLifetime : IConnectionLifetimeNotificationFeature
        {
            public CancellationToken ConnectionClosedRequested { get; set; }

            private readonly TaskCompletionSource _tcs;
            private readonly CancellationTokenSource _connectionClosedRequestedTokenSource;

            public TerminalConnectionLifetime
            (
                TaskCompletionSource tcs,
                CancellationTokenSource connectionClosedRequestedTokenSource
            )
            {
                this._tcs = tcs;
                this._connectionClosedRequestedTokenSource = connectionClosedRequestedTokenSource;

                this.ConnectionClosedRequested = connectionClosedRequestedTokenSource.Token;
            }

            public void RequestClose()
            {
                //run callback if any
                this._connectionClosedRequestedTokenSource.Cancel();

                //then set result, so CancellationTokenSource can get disposed
                this._tcs.TrySetResult();
            }
        }
    }
}

