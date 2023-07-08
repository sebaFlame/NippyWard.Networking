using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Connections.Middleware
{
    public class ProtocolReader<TMessage> : IProtocolReader<TMessage>, IDisposable
    {
        private readonly PipeReader _pipeReader;
        private readonly IMessageReader<TMessage> _reader;
        private readonly ILogger? _logger;

        private bool _disposed;
        private ReadOnlySequence<byte> _buffer;
        private bool _isCanceled;
        private bool _isCompleted;

        public ProtocolReader
        (
            PipeReader pipeReader,
            IMessageReader<TMessage> reader,
            ILogger? logger = null
        )
        {
            this._pipeReader = pipeReader;
            this._reader = reader;
            this._logger = logger;

            this._buffer = default;
        }

        public void Complete(Exception? ex = null)
            => this._pipeReader.Complete(ex);

        public ValueTask CompleteAsync(Exception? ex = null)
            => this._pipeReader.CompleteAsync(ex);

        public ValueTask<ProtocolReadResult<TMessage>> ReadMessageAsync
        (
            CancellationToken cancellationToken = default
        )
        {
            if(this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }

            //always get the correct transport
            PipeReader pipeReader = this._pipeReader;

            //buffer is empty, do a read
            if (this._buffer.IsEmpty
                && !this._isCompleted)
            {
                return this.ReadMessageAsync(pipeReader, cancellationToken);
            }

            //a buffer still exists, try reading a message from it
            ReadResult result = new ReadResult
            (
                this._buffer,
                this._isCanceled,
                this._isCompleted
            );
            ProtocolReadResult<TMessage> protocolReadResult;

            if (this.TrySetMessage
            (
                pipeReader,
                in result,
                this._reader,
                out protocolReadResult
            ))
            {
                return ValueTask.FromResult(protocolReadResult);
            }

            //no message has been found, continue reading from pipereader
            return this.ReadMessageAsync(pipeReader, cancellationToken);
        }

        private ValueTask<ProtocolReadResult<TMessage>> ReadMessageAsync
        (
            PipeReader pipeReader,
            CancellationToken cancellationToken = default
        )
        {
            ReadResult result;
            ProtocolReadResult<TMessage> protocolReadResult;

            while (pipeReader.TryRead(out result))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.TrySetMessage
                (
                    pipeReader,
                    in result,
                    this._reader,
                    out protocolReadResult
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
                    return this.ContinueDoAsyncRead
                    (
                        pipeReader,
                        readTask,
                        this._reader,
                        cancellationToken
                    );
                }

                if(this.TrySetMessage
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

        private async ValueTask<ProtocolReadResult<TMessage>>
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

                if(this.TrySetMessage
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

        private bool TrySetMessage
        (
            PipeReader pipeReader,
            in ReadResult result,
            IMessageReader<TMessage> reader,
            out ProtocolReadResult<TMessage> readResult
        )
        {
            this._buffer = result.Buffer;
            this._isCompleted = result.IsCompleted;
            this._isCanceled = result.IsCanceled;

            TMessage? message;

            if (this._isCanceled)
            {
                readResult = new ProtocolReadResult<TMessage>
                (
                    default,
                    this._isCanceled,
                    this._isCompleted,
                    this._buffer.Start,
                    this._buffer.Start
                );

                return true;
            }

            if (TryParseMessage
            (
                this._buffer,
                reader,
                out SequencePosition consumed,
                out SequencePosition examined,
                out message
            ))
            {
                //slice the buffer up until where it has been consumed
                //this way more messages can be read from the buffer
                this._buffer = this._buffer.Slice(consumed);

                readResult = new ProtocolReadResult<TMessage>
                (
                    message,
                    this._isCanceled,
                    //ensure not completed, because the buffer might still
                    //contain a message
                    this._isCompleted && this._buffer.IsEmpty,
                    consumed,
                    examined
                );

                //DO NOT advance, the (zero-copy) buffer still needs to be used
                return true;
            }

            //reset the buffer if no message can be parsed
            this._buffer = default;

            //no message found and pipereader completed, return complete message
            if(this._isCompleted)
            {
                readResult = new ProtocolReadResult<TMessage>
                (
                    default,
                    this._isCanceled,
                    this._isCompleted,
                    this._buffer.Start,
                    this._buffer.Start
                );

                return true;
            }

            //no message found and not completed, ensure more data becomes
            //available to retry parsing a message
            pipeReader.AdvanceTo(consumed, examined);

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
        {
            if(buffer.IsEmpty)
            {
                consumed = buffer.Start;
                examined = buffer.Start;
                message = default;
                return false;
            }

            return reader.TryParseMessage
            (
                buffer,
                out consumed,
                out examined,
                out message
            );
        }

        public void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }

            //do not try to advance when pipereader already completed
            //currently parsing from this._buffer
            if (this._isCompleted)
            {
                return;
            }

            //there is still buffer left do not advance untill all messages 
            //have been parsed, this ensures 1 read - 1 advance
            if(!this._buffer.IsEmpty)
            {
                return;
            }

            this._pipeReader.AdvanceTo(consumed, examined);
        }

        public void Dispose()
        {
            this._disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
