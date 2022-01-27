using System;
using System.Threading.Tasks;
using System.IO.Pipelines;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections
{
    public class Protocol<TMessage> : IConnectionProtocolFeature<TMessage>
    {
        public ProtocolReader<TMessage> Reader { get; private set; }
        public ProtocolWriter<TMessage> Writer { get; private set; }

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

        public void Complete(Exception ex)
        {
            this.Reader?.Complete(ex);
            this.Writer?.Complete(ex);
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

            this.Reader = reader = new ProtocolReader<TMessage>
            (
                pipe,
                this._messageReader
            );

            this.Writer = writer = new ProtocolWriter<TMessage>
            (
                pipe,
                this._messageWriter
            );

            connectionContext.Features
                .Set<IConnectionProtocolFeature<TMessage>>(this);
            connectionContext.Features.Set<IConnectionTerminalFeature>(this);

            await this._messageDispatcher
                .OnConnectedAsync(connectionContext)
                .ConfigureAwait(false);

            ValueTask<ProtocolReadResult<TMessage>> readResultTask;
            ProtocolReadResult<TMessage> readResult;

            try
            {
                using(reader)
                {
                    using(writer)
                    {
                        while(true)
                        {
                            readResultTask = reader.ReadMessageAsync();

                            if(readResultTask.IsCompletedSuccessfully)
                            {
                                readResult = readResultTask.Result;
                            }
                            else
                            {
                                readResult = await readResultTask
                                    .ConfigureAwait(false);
                            }

                            if(readResult.IsCanceled
                               || readResult.IsCompleted)
                            {
                                break;
                            }

                            await this._messageDispatcher
                                .DispatchMessageAsync(readResult.Message)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                await this._messageDispatcher
                    .OnDisconnectedAsync(connectionContext)
                    .ConfigureAwait(false);
            }
        }
    }
}
