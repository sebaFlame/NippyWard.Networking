using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;

namespace ThePlague.Networking.Connections.Middleware
{
    public class ProtocolReader<TMessage> : IProtocolReader<TMessage>, IDisposable
    {
        private readonly IDuplexPipe _pipe;
        private readonly IMessageReader<TMessage> _reader;

        private bool _disposed;

        public ProtocolReader
        (
            IDuplexPipe pipe,
            IMessageReader<TMessage> reader
        )
        {
            this._pipe = pipe;
            this._reader = reader;
        }

        public void Complete(Exception ex = null)
            => this._pipe.Input.Complete(ex);

        public ValueTask<ProtocolReadResult<TMessage>> ReadMessageAsync(CancellationToken cancellationToken = default)
        {
            if(this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }

            //always get the correct transport
            PipeReader pipeReader = this._pipe.Input;

            while(pipeReader.TryRead(out ReadResult result))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if(TrySetMessage
                (
                    pipeReader,
                    in result,
                    this._reader,
                    out ProtocolReadResult<TMessage> protocolReadResult
                ))
                {
                    return ValueTask.FromResult(protocolReadResult);
                }
            }

            return this.DoAsyncRead(pipeReader, cancellationToken);
        }

        private ValueTask<ProtocolReadResult<TMessage>> DoAsyncRead
        (
            PipeReader pipeReader,
            CancellationToken cancellationToken = default
        )
        {
            while(true)
            {
                ValueTask<ReadResult> readTask = pipeReader.ReadAsync(cancellationToken);
                ReadResult result;

                if(readTask.IsCompletedSuccessfully)
                {
                    result = readTask.Result;
                }
                else
                {
                    return ContinueDoAsyncRead
                    (
                        pipeReader,
                        readTask,
                        this._reader,
                        cancellationToken
                    );
                }

                if(TrySetMessage
                (
                    pipeReader,
                    in result,
                    this._reader,
                    out ProtocolReadResult<TMessage> protocolReadResult
                ))
                {
                    return ValueTask.FromResult(protocolReadResult);
                }
            }
        }

        private static async ValueTask<ProtocolReadResult<TMessage>>
            ContinueDoAsyncRead
        (
            PipeReader pipeReader,
            ValueTask<ReadResult> readTask,
            IMessageReader<TMessage> reader,
            CancellationToken cancellationToken = default
        )
        {
            while(true)
            {
                ReadResult result = await readTask;

                if(TrySetMessage
                (
                    pipeReader,
                    in result,
                    reader,
                    out ProtocolReadResult<TMessage> protocolReadResult
                ))
                {
                    return protocolReadResult;
                }

                readTask = pipeReader.ReadAsync(cancellationToken);
            }
        }

        private static bool TrySetMessage
        (
            PipeReader pipeReader,
            in ReadResult result,
            IMessageReader<TMessage> reader,
            out ProtocolReadResult<TMessage> readResult
        )
        {
            bool isCompleted = result.IsCompleted;
            bool isCanceled = result.IsCanceled;

            if(isCanceled)
            {
                readResult = default;
                return true;
            }

            SequencePosition consumed = default, examined = default;
            ReadOnlySequence<byte> buffer = result.Buffer;
            TMessage message;

            if(TryParseMessage
            (
                buffer,
                reader,
                ref consumed,
                ref examined,
                out message
            ))
            {
                pipeReader.AdvanceTo(consumed, examined);
                readResult = new ProtocolReadResult<TMessage>
                (
                    message,
                    false,
                    false
                );
                return true;
            }
            else
            {
                pipeReader.AdvanceTo(consumed, examined);
            }

            if(isCompleted)
            {
                readResult = new ProtocolReadResult<TMessage>
                (
                    default,
                    isCanceled,
                    isCompleted
                );
                return true;
            }

            readResult = default;
            return false;
        }

        private static bool TryParseMessage
        (
            in ReadOnlySequence<byte> buffer,
            IMessageReader<TMessage> reader,
            ref SequencePosition consumed,
            ref SequencePosition examined,
            out TMessage message
        )
            => reader.TryParseMessage
            (
                buffer,
                ref consumed,
                ref examined,
                out message
            );

        public void Dispose()
        {
            this.Complete(null);
            this._disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
