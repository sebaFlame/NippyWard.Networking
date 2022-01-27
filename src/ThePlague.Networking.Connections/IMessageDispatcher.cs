using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections
{
    public interface IMessageDispatcher<TMessage>
    {
        public Task OnConnectedAsync
        (
            ConnectionContext connectionContext
        );

        public Task DispatchMessageAsync
        (
            TMessage message
        );

        public Task OnDisconnectedAsync
        (
            ConnectionContext connectionContext
        );
    }
}