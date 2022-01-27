using System;
using System.Threading.Tasks;
using System.IO.Pipelines;

using Microsoft.AspNetCore.Connections;

using ThePlague.Networking.Pipelines;

namespace ThePlague.Networking.Connections
{
    internal class ConnectionTerminal : IConnectionTerminalFeature
    {
        internal TaskCompletionSource<object> ConnectionCompleted
            => this._taskCompletion;

        private readonly TaskCompletionSource<object> _taskCompletion;

        public ConnectionTerminal()
        {
            this._taskCompletion = new TaskCompletionSource<object>();
        }

        public void Complete(Exception ex)
        {
            if(ex is null)
            {
                this._taskCompletion.TrySetResult(null);
            }
            else
            {
                this._taskCompletion.TrySetException(ex);
            }
        }

        internal async Task OnConnectionAsync
        (
            ConnectionContext connectionContext
        )
        {
            IDuplexPipe originalTransport = connectionContext.Transport;

            IDuplexPipe newTransport = new DuplexPipe
            (
                new TerminalPipeReader(originalTransport, this),
                new TerminalPipeWriter(originalTransport, this)
            );

            connectionContext.Transport = newTransport;

            connectionContext.Features.Set<IConnectionTerminalFeature>(this);

            Exception error = null;

            //it needs to block on the terminal
            try
            {
                await this._taskCompletion.Task.ConfigureAwait(false);
            }
            catch(Exception ex)
            {
                error = ex;
            }
            finally
            {
                //ensure the transport gets completed
                originalTransport.Input.Complete(error);
                originalTransport.Output.Complete(error);

                //restore original transport
                connectionContext.Transport = originalTransport;
            }
        }
    }
}

