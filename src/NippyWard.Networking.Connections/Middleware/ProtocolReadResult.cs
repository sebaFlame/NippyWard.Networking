using System;

namespace NippyWard.Networking.Connections.Middleware
{
    public readonly struct ProtocolReadResult<TMessage>
    {
        public ProtocolReadResult
        (
            TMessage? message,
            bool isCanceled,
            bool isCompleted,
            SequencePosition consumed,
            SequencePosition examined
        )
        {
            this.Message = message;
            this.IsCanceled = isCanceled;
            this.IsCompleted = isCompleted;
            this.Consumed = consumed;
            this.Examined = examined;
        }

        public TMessage? Message { get; }
        public bool IsCanceled { get; }
        public bool IsCompleted { get; }
        public SequencePosition Examined { get; }
        public SequencePosition Consumed { get; }
    }
}
