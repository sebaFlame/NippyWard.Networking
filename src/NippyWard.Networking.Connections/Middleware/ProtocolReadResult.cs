namespace NippyWard.Networking.Connections.Middleware
{
    public readonly struct ProtocolReadResult<TMessage>
    {
        public ProtocolReadResult
        (
            TMessage? message,
            bool isCanceled,
            bool isCompleted
        )
        {
            this.Message = message;
            this.IsCanceled = isCanceled;
            this.IsCompleted = isCompleted;
        }

        public TMessage? Message { get; }
        public bool IsCanceled { get; }
        public bool IsCompleted { get; }
    }
}
