using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using OpenSSL.Core.SSL.Buffer;

namespace ThePlague.Networking.Tls
{
    internal class TlsPipeReader : PipeReader
    {
        private readonly TlsPipe _tlsPipe;

        public TlsPipeReader
        (
            TlsPipe tlsPipe
        )
        {
            this._tlsPipe = tlsPipe;
        }

        public override void AdvanceTo(SequencePosition consumed)
            => this._tlsPipe.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
            => this._tlsPipe.AdvanceTo(consumed, examined);

        public override void CancelPendingRead()
            => this._tlsPipe.CancelPendingRead();

        public override void Complete(Exception exception = null)
            => this._tlsPipe.CompleteReader(exception);

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => this._tlsPipe.ReadAsync(cancellationToken);

        public override bool TryRead(out ReadResult result)
            => this._tlsPipe.TryRead(out result);
    }
}
