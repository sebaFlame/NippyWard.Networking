using System;
using System.Threading;
using System.Threading.Tasks;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections.Middleware
{
    public class Protocol<TMessage> : IConnectionProtocolFeature<TMessage>
    {
        public IMessageReader<TMessage> Reader => this._messageReader;
        public IMessageWriter<TMessage> Writer => this._messageWriter;
        public IMessageDispatcher<TMessage> Dispatcher => this._messageDispatcher;

        private readonly IMessageReader<TMessage> _messageReader;
        private readonly IMessageWriter<TMessage> _messageWriter;
        private readonly IMessageDispatcher<TMessage> _messageDispatcher;

        public Protocol
        (
            IMessageReader<TMessage> messageReader,
            IMessageWriter<TMessage> messageWriter,
            IMessageDispatcher<TMessage> messageDispatcher
        )
        {
            this._messageReader = messageReader;
            this._messageWriter = messageWriter;
            this._messageDispatcher = messageDispatcher;
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
            CancellationTokenRegistration? regReader = null, regWriter = null;
            CancellationTokenSource cts = null;

            reader = new ProtocolReader<TMessage>
            (
                pipe,
                this._messageReader
            );

            writer = new ProtocolWriter<TMessage>
            (
                pipe,
                this._messageWriter
            );

            //register abort to reader/writer
            IConnectionLifetimeFeature connectionLifetimeFeature = connectionContext.Features.Get<IConnectionLifetimeFeature>();
            if (connectionLifetimeFeature is not null)
            {
                regReader = connectionLifetimeFeature
                    .ConnectionClosed
                    .Register((r) => ((ProtocolReader<TMessage>)r).Complete(new ConnectionAbortedException()), reader, false);

                regWriter = connectionLifetimeFeature
                    .ConnectionClosed
                    .Register((w) => ((ProtocolWriter<TMessage>)w).Complete(new ConnectionAbortedException()), writer, false);
            }

            //set correct connection features
            connectionContext.Features.Set<IProtocolReader<TMessage>>(reader);
            connectionContext.Features.Set<IProtocolWriter<TMessage>>(writer);

            connectionContext.Features.Set<IConnectionProtocolFeature<TMessage>>(this);

            IConnectionLifetimeNotificationFeature connectionLifetimeNotificationFeature
                = connectionContext.Features.Get<IConnectionLifetimeNotificationFeature>();

            CancellationToken cancellationToken = connectionContext.ConnectionClosed;
            if(connectionLifetimeNotificationFeature is null)
            {
                cts = new CancellationTokenSource();

                connectionContext.Features
                    .Set<IConnectionLifetimeNotificationFeature>
                (
                    new ProtocolConnectionLifetime
                    (
                        reader,
                        writer,
                        cts
                    )
                );
            }
            else
            {

                cts = CancellationTokenSource.CreateLinkedTokenSource
                (
                    connectionLifetimeNotificationFeature.ConnectionClosedRequested,
                    connectionContext.ConnectionClosed
                );
                cancellationToken = cts.Token;
            }

            try
            {
                await this._messageDispatcher.OnConnectedAsync(connectionContext);

                ValueTask<ProtocolReadResult<TMessage>> readResultTask;
                ProtocolReadResult<TMessage> readResult;

                while(true)
                {
                    readResultTask = reader.ReadMessageAsync(cancellationToken);

                    if(readResultTask.IsCompletedSuccessfully)
                    {
                        readResult = readResultTask.Result;
                    }
                    else
                    {
                        readResult = await readResultTask;
                    }

                    if(readResult.IsCanceled
                        || readResult.IsCompleted)
                    {
                        break;
                    }

                    await this._messageDispatcher.DispatchMessageAsync
                    (
                        connectionContext,
                        readResult.Message
                    );
                }

                await this._messageDispatcher.OnDisconnectedAsync(connectionContext);
            }
            catch(Exception ex)
            {
                reader.Complete(ex);
                writer.Complete(ex);

                throw;
            }
            finally
            {
                regReader?.Dispose();
                regWriter?.Dispose();

                reader.Dispose();
                writer.Dispose();

                cts?.Dispose();
            }
        }

        private class ProtocolConnectionLifetime : IConnectionLifetimeNotificationFeature
        {
            public CancellationToken ConnectionClosedRequested { get; set; }

            private readonly CancellationTokenSource _connectionClosedRequestedTokenSource;
            private readonly ProtocolReader<TMessage> _reader;
            private readonly ProtocolWriter<TMessage> _writer;

            public ProtocolConnectionLifetime
            (
                ProtocolReader<TMessage> reader,
                ProtocolWriter<TMessage> writer,
                CancellationTokenSource connectionClosedRequestedTokenSource
            )
            {
                this._reader = reader;
                this._writer = writer;
                this._connectionClosedRequestedTokenSource = connectionClosedRequestedTokenSource;
            }

            public void RequestClose()
            {
                //first fire callbacks
                this._connectionClosedRequestedTokenSource.Cancel();

                //then complete reader/writer, so the CancellationTokenSource can get disposed
                this._reader.Complete();
                this._writer.Complete();
            }
        }
    }
}
