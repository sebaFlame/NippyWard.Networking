using Microsoft.AspNetCore.Connections.Features;

namespace ThePlague.Networking.Connections.Middleware
{
    public interface IConnectionProtocolFeature<TMessage>
    {
        IMessageReader<TMessage> Reader { get; }
        IMessageWriter<TMessage> Writer { get; }
        IMessageDispatcher<TMessage> Dispatcher { get; }
    }
}