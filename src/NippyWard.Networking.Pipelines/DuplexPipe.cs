using System.IO.Pipelines;

namespace NippyWard.Networking.Pipelines
{
    public class DuplexPipe : IDuplexPipe
    {
        public PipeReader Input => this._reader;
        public PipeWriter Output => this._writer;

        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        public DuplexPipe
        (
            PipeReader reader,
            PipeWriter writer
        )
        {
            this._reader = reader;
            this._writer = writer;
        }
    }
}
