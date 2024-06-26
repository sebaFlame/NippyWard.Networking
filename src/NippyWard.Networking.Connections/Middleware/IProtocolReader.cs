﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NippyWard.Networking.Connections.Middleware
{
    public interface IProtocolReader<TMessage>
    {
        void Complete(Exception? ex = null);

        ValueTask CompleteAsync(Exception? exception = null);

        ValueTask<ProtocolReadResult<TMessage>> ReadMessageAsync(CancellationToken cancellationToken = default);

        void AdvanceTo(SequencePosition consumed, SequencePosition examined);
    }
}
