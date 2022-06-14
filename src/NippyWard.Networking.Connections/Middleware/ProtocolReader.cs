using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace NippyWard.Networking.Connections.Middleware
{
    public class ProtocolReader<TMessage> : IProtocolReader<TMessage>, IDisposable
    {
        private readonly PipeReader _pipeReader;
        private readonly IMessageReader<TMessage> _reader;
        private readonly ILogger? _logger;

        private bool _disposed;

        public ProtocolReader
        (
            PipeReader pipeReader,
            IMessageReader<TMessage> reader,
            ILogger? logger = null
        )
        {
            this._pipeReader = pipeReader;
            this._reader = reader;
        }

        public void Complete(Exception? ex = null)
            => this._pipeReader.Complete(ex);

        public ValueTask CompleteAsync(Exception? ex = null)
            => this._pipeReader.CompleteAsync(ex);

        public ValueTask<ProtocolReadResult<TMessage>> ReadMessageAsync(CancellationToken cancellationToken = default)
        {
            if(this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }

            //always get the correct transport
            PipeReader pipeReader = this._pipeReader;

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
                readResult = new ProtocolReadResult<TMessage>
                (
                    default,
                    isCanceled,
                    isCompleted
                );

                return true;
            }

            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start, examined = buffer.Start;
            TMessage? message;

            try
            {
                if (TryParseMessage
                (
                    buffer,
                    reader,
                    out consumed,
                    out examined,
                    out message
                ))
                {
                    readResult = new ProtocolReadResult<TMessage>
                    (
                        message,
                        false,
                        false
                    );

                    return true;
                }
            }
            finally
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
            out SequencePosition consumed,
            out SequencePosition examined,
            [NotNullWhen(true)] out TMessage? message
        )
            => reader.TryParseMessage
            (
                buffer,
                out consumed,
                out examined,
                out message
            );

        public void Dispose()
        {
            this._disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
