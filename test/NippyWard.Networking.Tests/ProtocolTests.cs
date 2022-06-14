using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.Threading;

using Xunit;
using Xunit.Sdk;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Connections;
using NippyWard.Networking.Connections.Middleware;
using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Tests
{
    public class ProtocolTests : BaseTests
    {
        private readonly ILogger _logger;

        internal const string _Hello = "Hello World\n";
        internal static ReadOnlyMemory<byte> _HelloBuffer;

        static ProtocolTests()
        {
            _HelloBuffer = new ReadOnlyMemory<byte>
            (
                MemoryMarshal.AsBytes<char>(_Hello).ToArray()
            );
        }

        public ProtocolTests(ServicesState serviceState)
            : base(serviceState)
        {
            this._logger = serviceState.ServiceProvider.CreateLogger<ProtocolTests>()!;
        }

        protected override ClientBuilder ConfigureClient(ClientBuilder clientBuilder)
            => clientBuilder;

        protected override ServerBuilder ConfigureServer(ServerBuilder serverBuilder)
            => serverBuilder;


        private class HelloReader : IMessageReader<string>
        {
            private long _position;
            private ILogger _logger;

            public HelloReader(ILogger logger)
            {
                this._position = 0;
                this._logger = logger;
            }

            public bool TryParseMessage
            (
                in ReadOnlySequence<byte> input,
                out SequencePosition consumed,
                out SequencePosition examined,
                [NotNullWhen(true)] out string? message
            )
            {
                consumed = input.Start;
                examined = input.End;

                if(input.IsEmpty)
                {
                    this._logger.TraceLog("HelloReader", "empty");

                    message = null;
                    return false;
                }

                this._position = input.GetOffset(examined);

                //verify with buffer length as "parsing"
                if (this._position < _HelloBuffer.Length)
                {
                    this._logger.TraceLog("HelloReader", "incomplete");

                    message = null;
                    return false;
                }

                this._logger.TraceLog("HelloReader", "complete");

                consumed = input.End;
                byte[] b = input.ToArray();
                ReadOnlySpan<char> c = MemoryMarshal.Cast<byte, char>(b);

                message = new string(c);
                return true;
            }
        }

        private class HelloWriter : IMessageWriter<string>
        {
            public void WriteMessage
            (
                string message,
                IBufferWriter<byte> output
            )
            {
                ReadOnlySpan<byte> s = MemoryMarshal.AsBytes<char>(message);
                Span<byte> b = output.GetSpan(s.Length);

                s.CopyTo(b);

                output.Advance(s.Length);
            }
        }

        private class HelloClientMessageDispatcher : IMessageDispatcher<string>
        {
            public async ValueTask DispatchMessageAsync
            (
                ConnectionContext connectionContext,
                string message,
                CancellationToken cancellationToken = default
            )
            {
                IProtocolWriter<string> writer
                    = connectionContext.Features.Get<IProtocolWriter<string>>()!;
                try
                {
                    Assert.Equal(_Hello, message);
                }
                finally
                {
                    //initiate shutdown
                    await writer.CompleteAsync();
                }
            }

            public ValueTask OnConnectedAsync
            (
                ConnectionContext connectionContext,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }

            public ValueTask OnDisconnectedAsync
            (
                ConnectionContext connectionContext,
                Exception? exception = null,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }
        }

        #region Full Hello
        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task Server_Send_Client_Full_Hello_With_Shutdown(EndPoint endpoint)
        {
            IMessageReader<string> reader = new HelloReader(this._logger);
            IMessageWriter<string> writer = new HelloWriter();
            IMessageDispatcher<string> clientDispatcher = new HelloClientMessageDispatcher();

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Send_Client_Full_Hello_With_Shutdown_server"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        new FullHelloServerMessageDispatcher()
                    )
                )
                .ConfigureMaxClients(1)
                .BuildServer();

            ClientBuilder clientBuilder = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Send_Client_Full_Hello_With_Shutdown_client"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        clientDispatcher
                    )
                );

            await using(server)
            {
                await server.StartAsync();

                await using(Client client = await clientBuilder.BuildClient(endpoint))
                {
                    await client.RunAsync();
                }

                await server.RunAsync();
            }
        }

        private class FullHelloServerMessageDispatcher : IMessageDispatcher<string>
        {
            public ValueTask DispatchMessageAsync
            (
                ConnectionContext connectionContext,
                string message,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }

            public async ValueTask OnConnectedAsync
            (
                ConnectionContext connectionContext,
                CancellationToken cancellationToken = default
            )
            {
                IProtocolWriter<string> writer
                    = connectionContext.Features.Get<IProtocolWriter<string>>()!;

                try
                {
                    await writer.WriteAsync(_Hello, cancellationToken);
                }
                finally
                {
                    //initiate shutdown
                    await writer.CompleteAsync();
                }
            }

            public ValueTask OnDisconnectedAsync
            (
                ConnectionContext connectionContext,
                Exception? exception = null,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }
        }
        #endregion

        #region Partial Hello
        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task Server_Send_Client_Partial_Hello_With_Shutdown(EndPoint endpoint)
        {
            IMessageReader<string> reader = new HelloReader(this._logger);
            IMessageWriter<string> writer = new HelloWriter();
            IMessageDispatcher<string> clientDispatcher = new HelloClientMessageDispatcher();

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Send_Client_Partial_Hello_With_Shutdown_server"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        new PartialHelloServerMessageDispatcher()
                    )
                )
                .ConfigureMaxClients(1)
                .BuildServer();

            ClientBuilder clientBuilder = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Send_Client_Partial_Hello_With_Shutdown_client"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        clientDispatcher
                    )
                );

            await using (server)
            {
                await server.StartAsync();

                await using (Client client = await clientBuilder.BuildClient(endpoint))
                {
                    await client.RunAsync();
                }

                await server.RunAsync();
            }
        }

        private class PartialHelloServerMessageDispatcher : IMessageDispatcher<string>
        {
            public ValueTask DispatchMessageAsync
            (
                ConnectionContext connectionContext,
                string message,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }

            public async ValueTask OnConnectedAsync
            (
                ConnectionContext connectionContext,
                CancellationToken cancellationToken = default
            )
            {
                IProtocolWriter<string> writer
                    = connectionContext.Features.Get<IProtocolWriter<string>>()!;

                try
                {
                    //send hello 1 char at a time
                    foreach (char c in _Hello)
                    {
                        await writer.WriteAsync($"{c}", cancellationToken);
                    }
                }
                finally
                {
                    //initiate shutdown
                    await writer.CompleteAsync();
                }
            }

            public ValueTask OnDisconnectedAsync
            (
                ConnectionContext connectionContext,
                Exception? exception = null,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }
        }
        #endregion

        #region Exception
        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task Server_Client_Exception(EndPoint endpoint)
        {
            IMessageReader<string> reader = new HelloReader(this._logger);
            IMessageWriter<string> writer = new HelloWriter();

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Client_Exception_server"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        new FullHelloServerMessageDispatcher()
                    )
                )
                .ConfigureMaxClients(1)
                .BuildServer();

            ClientBuilder clientBuilder = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => "Server_Client_Exception_client"
                        )
                )
                .ConfigureConnection
                (
                    (c) => c.UseProtocol<string>
                    (
                        reader,
                        writer,
                        new ExceptionlientMessageDispatcher()
                    )
                );

            await using (server)
            {
                await server.StartAsync();

                await using (Client client = await clientBuilder.BuildClient(endpoint))
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.RunAsync());
                }

                await server.RunAsync();
            }
        }

        private class ExceptionlientMessageDispatcher : IMessageDispatcher<string>
        {
            public ValueTask DispatchMessageAsync
            (
                ConnectionContext connectionContext,
                string message,
                CancellationToken cancellationToken = default
            )
            {
                throw new InvalidOperationException();
            }

            public ValueTask OnConnectedAsync
            (
                ConnectionContext connectionContext,
                CancellationToken cancellationToken = default
            )
            {
                return default;
            }

            public ValueTask OnDisconnectedAsync
            (
                ConnectionContext connectionContext,
                Exception? exception = null,
                CancellationToken cancellationToken = default
            )
            {
                Assert.NotNull(exception);
                Assert.IsType<AggregateException>(exception);

                return default;
            }
        }
        #endregion
    }
}
