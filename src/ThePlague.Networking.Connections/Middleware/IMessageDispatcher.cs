using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections.Middleware
{
    public interface IMessageDispatcher<TMessage>
    {
        public Task OnConnectedAsync
        (
            ConnectionContext connectionContext
        );

        public Task DispatchMessageAsync
        (
            ConnectionContext connectionContext,
            TMessage message
        );

        public Task OnDisconnectedAsync
        (
            ConnectionContext connectionContext
        );
    }
}