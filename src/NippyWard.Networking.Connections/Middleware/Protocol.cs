using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Connections;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Connections.Middleware
{
    public class Protocol<TMessage>
        where TMessage : class
    {
        public IMessageReader<TMessage> Reader => this._messageReader;
        public IMessageWriter<TMessage> Writer => this._messageWriter;
        public IMessageDispatcher<TMessage> Dispatcher => this._messageDispatcher;

        private readonly IMessageReader<TMessage> _messageReader;
        private readonly IMessageWriter<TMessage> _messageWriter;
        private readonly IMessageDispatcher<TMessage> _messageDispatcher;
        private readonly ILogger? _logger;

        public Protocol
        (
            IMessageReader<TMessage> messageReader,
            IMessageWriter<TMessage> messageWriter,
            IMessageDispatcher<TMessage> messageDispatcher,
            ILogger? logger = null
        )
        {
            this._messageReader = messageReader;
            this._messageWriter = messageWriter;
            this._messageDispatcher = messageDispatcher;
            this._logger = logger;
        }

        //this should always be the terminal delegate and "block"
        internal async Task OnConnectionAsync
        (
            ConnectionContext connectionContext
        )
        {
            ProtocolReader<TMessage> reader;
            ProtocolWriter<TMessage> writer;
            IDuplexPipe pipe = connectionContext.Transport;
            ValueTask dispatcherTask;
            Exception? error = null;
            PipeReader pipeReader = pipe.Input;

            reader = new ProtocolReader<TMessage>
            (
                pipeReader,
                this._messageReader,
                this._logger
            );

            writer = new ProtocolWriter<TMessage>
            (
                pipe.Output,
                this._messageWriter,
                this._logger
            );

            //set correct connection feature
            connectionContext.Features.Set<IProtocolWriter<TMessage>>(writer);

            CancellationToken cancellationToken = connectionContext.ConnectionClosed;

            try
            {
                this._logger?.TraceLog
                (
                    connectionContext.ConnectionId,
                    "Protocol connected"
                );

                dispatcherTask = this._messageDispatcher.OnConnectedAsync
                (
                    connectionContext,
                    cancellationToken
                );

                if(!dispatcherTask.IsCompleted)
                {
                    await dispatcherTask;
                }

                ValueTask<ProtocolReadResult<TMessage>> readResultTask;
                ProtocolReadResult<TMessage> readResult;

                while(true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    this._logger?.TraceLog
                    (
                        connectionContext.ConnectionId,
                        "protocol read initialized"
                    );

                    //if shutdown initiater -> await 0 byte read
                    //else await new message
                    readResultTask = reader.ReadMessageAsync(cancellationToken);

                    if(readResultTask.IsCompleted)
                    {
                        readResult = readResultTask.Result;
                        this._logger?.TraceLog
                        (
                            connectionContext.ConnectionId,
                            "protocol sync read"
                        );
                    }
                    else
                    {
                        readResult = await readResultTask;
                        this._logger?.TraceLog
                        (
                            connectionContext.ConnectionId,
                            "protocol async read"
                        );
                    }

                    //check what got canceled, can only happen when consumer
                    //uses Transport (which is possible!).
                    if (readResult.IsCanceled)
                    {
                        this._logger?.TraceLog
                        (
                            connectionContext.ConnectionId,
                            "protocol read cancelled"
                        );

                        continue;
                    }

                    if (readResult.Message is null)
                    {
                        if (readResult.IsCompleted)
                        {
                            this._logger?.TraceLog
                            (
                                connectionContext.ConnectionId,
                                "protocol read completed"
                            );

                            break;
                        }

                        //should not happen
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    this._logger?.TraceLog
                    (
                        connectionContext.ConnectionId,
                        "protocol dispatching read message"
                    );

                    //message consumer - DANGER
                    dispatcherTask = this._messageDispatcher.DispatchMessageAsync
                    (
                        connectionContext,
                        readResult.Message,
                        cancellationToken
                    );

                    if (!dispatcherTask.IsCompleted)
                    {
                        await dispatcherTask;
                    }

                    //message has been consumed, buffer can be thrown away
                    if (readResult.IsCompleted)
                    {
                        this._logger?.TraceLog
                        (
                            connectionContext.ConnectionId,
                            "protocol read completed"
                        );

                        break;
                    }

                    //mark message as consumed and free buffers AFTER the
                    //message has been dispatched
                    reader.AdvanceTo(readResult.Consumed, readResult.Examined);
                }

                //DO NOT CALL NEXT
            }
            //ignore this cancelation because it was caused by ConnectionClosed
            catch(OperationCanceledException ex)
            {
                if(ex.CancellationToken != cancellationToken)
                {
                    throw;
                }
            }
            catch(Exception ex)
            {
                this._logger?.TraceLog
                (
                    connectionContext.ConnectionId,
                    $"protocol fail: {ex.Message}"
                );

                error = ex;
                throw;
            }
            finally
            {
                Exception? writerEx = null, readerEx = null;

                //ensure graceful shutdown

                try
                {
                    await writer.CompleteAsync(error);
                }
                catch(Exception e)
                {
                    this._logger?.TraceLog
                    (
                        connectionContext.ConnectionId,
                        $"writer fail: {e.Message}"
                    );

                    writerEx = e;
                }

                try
                {
                    await reader.CompleteAsync(error);
                }
                catch (Exception e)
                {
                    this._logger?.TraceLog
                    (
                        connectionContext.ConnectionId,
                        $"reader fail: {e.Message}"
                    );

                    readerEx = e;
                }

                //pass execptions to consumer during disconnect
                Exception?[] ex = new Exception?[]
                {
                    error,
                    writerEx,
                    readerEx
                };

                if (ex.Any(static x => x is not null))
                {
                    error = new AggregateException
                    (
                        ex
                            .Where(static x => x is not null)
                            .Select(static y => y!)
                    );
                }

                this._logger?.TraceLog
                (
                    connectionContext.ConnectionId,
                    "Protocol disconnected"
                );

                try
                {
                    dispatcherTask = this._messageDispatcher.OnDisconnectedAsync
                    (
                        connectionContext,
                        error
                    );

                    if (!dispatcherTask.IsCompleted)
                    {
                        await dispatcherTask;
                    }
                }
                finally
                {
                    writer.Dispose();
                    reader.Dispose();
                }
            }
        }
    }
}
