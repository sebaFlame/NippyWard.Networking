namespace ThePlague.Networking.Connections
{
    public interface IConnectionProtocolFeature<TMessage>
        : IConnectionTerminalFeature
    {
        public ProtocolReader<TMessage> Reader { get; }
        public ProtocolWriter<TMessage> Writer { get; }
    }
}