using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NippyWard.Networking.Connections.Middleware
{
    public interface IProtocolWriter<TMessage>
    {
        void Complete(Exception? ex = null);

        ValueTask CompleteAsync(Exception? ex = null);

        ValueTask WriteAsync
        (
            TMessage message,
            CancellationToken cancellationToken = default
        );
    }
}
