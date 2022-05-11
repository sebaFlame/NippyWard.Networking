using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Tls.Buffer;

namespace ThePlague.Networking.Tls
{
    internal class TlsPipeWriter : PipeWriter
    {
        private readonly TlsPipe _tlsPipe;

        public TlsPipeWriter
        (
            TlsPipe tlsPipe
        )
        {
            this._tlsPipe = tlsPipe;
        }

        public override bool CanGetUnflushedBytes
            => this._tlsPipe.CanGetUnflushedBytes;

        public override long UnflushedBytes
            => this._tlsPipe.UnflushedBytes;

        public override void Advance(int bytes)
            => this._tlsPipe.Advance(bytes);

        public override void CancelPendingFlush()
            => this._tlsPipe.CancelPendingFlush();

        public override void Complete(Exception exception = null)
            => this._tlsPipe.CompleteWriter(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => this._tlsPipe.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
            => this._tlsPipe.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0)
            => this._tlsPipe.GetSpan(sizeHint);
    }
}
