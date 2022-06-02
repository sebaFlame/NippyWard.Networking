using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

using Xunit;
using Xunit.Sdk;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;

using ThePlague.Networking.Transports.Pipes;
using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Tests
{
    public class NamedPipeTests : IClassFixture<ServicesState>
    {
        private const string _Name = "test";
        private const string _ServerHello = "Hello Client";
        private const string _ClientHello = "Hello Server";

        internal IServiceProvider ServiceProvider => this._servicesState.ServiceProvider;

        private ServicesState _servicesState;

        private static int _PipeIndex;

        static NamedPipeTests()
        {
            _PipeIndex = 0;
        }

        public NamedPipeTests(ServicesState serviceState)
        {
            this._servicesState = serviceState;
        }

        private static NamedPipeEndPoint CreateEndPoint()
        {
            int index = Interlocked.Increment(ref _PipeIndex);
            return new NamedPipeEndPoint(string.Concat(_Name, "_", index));
        }

        public static IEnumerable<object[]> GetEndPoint() => new object[][]
        {
            new object[] { CreateEndPoint() }
        };

        internal static IConnectionBuilder ConfigureCloseInitializer(IConnectionBuilder connectionBuilder)
            => connectionBuilder.Use
            (
                next =>
                async (ConnectionContext ctx) =>
                {
                    await Task.Delay(100);

                    try
                    {
                        //initialize read, or it might complete before you start
                        //reading
                        ValueTask<ReadResult> readTask = ctx.Transport.Input.ReadAsync();

                        //close connection
                        await ctx.Transport.Output.CompleteAsync();

                        //await confirmation close from peer
                        ReadResult readResult = await readTask;
                        Assert.True(readResult.IsCompleted);
                        Assert.True(readResult.Buffer.IsEmpty);
                    }
                    finally
                    {
                        await ctx.Transport.Input.CompleteAsync();
                    }
                }
            );

        internal static IConnectionBuilder ConfigureCloseListener(IConnectionBuilder connectionBuilder)
            => connectionBuilder.Use
            (
                next =>
                async (ConnectionContext ctx) =>
                {
                    try
                    {
                        //await connection close
                        ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                        Assert.True(readResult.IsCompleted);
                        Assert.True(readResult.Buffer.IsEmpty);
                    }
                    finally
                    {
                        //confirm close
                        await ctx.Transport.Output.CompleteAsync();
                        await ctx.Transport.Input.CompleteAsync();
                    }
                }
            );

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ConnectServerCloseTest(NamedPipeEndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server =  new ServerBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    endpoint,
                    () => $"ConnectServerCloseTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(1)
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .BuildServer();

            await using(server)
            {
                await server.StartAsync();

                Client client = await new ClientBuilder(this.ServiceProvider)
                    .UseNamedPipe
                    (
                        () => $"ConnectServerCloseTest_client_{clientIndex++}_{endpoint}"
                    )
                    .ConfigureConnection((c) => ConfigureCloseListener(c))
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ConnectClientCloseTest(NamedPipeEndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    endpoint,
                    () => $"ConnectClientCloseTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(1)
                .ConfigureConnection((c) => ConfigureCloseListener(c))
                .BuildServer();

            await using (server)
            {
                await server.StartAsync();

                Client client = await new ClientBuilder(this.ServiceProvider)
                    .UseNamedPipe
                    (
                        () => $"ConnectClientCloseTest_client_{clientIndex++}_{endpoint}"
                    )
                    .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ServerDataTest(NamedPipeEndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            byte[] result = new byte[_ServerHello.Length * 2];

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    endpoint,
                    () => $"ServerDataTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeWriter writer = ctx.Transport.Output;

                                Memory<byte> buffer = writer.GetMemory(_ServerHello.Length * 2);
                                MemoryMarshal.AsBytes<char>(_ServerHello).CopyTo(buffer.Span);

                                writer.Advance(_ServerHello.Length * 2);

                                await writer.FlushAsync();

                                await next(ctx);
                            }
                        )
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    () => $"ServerDataTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeReader reader = ctx.Transport.Input;

                                ReadResult readResult = await reader.ReadAsync();

                                ReadOnlySequence<byte> buffer = readResult.Buffer;
                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                await next(ctx);
                            }
                        )
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True
            (
                new ReadOnlySpan<byte>(result)
                    .SequenceEqual(MemoryMarshal.AsBytes<char>(_ServerHello))
            );
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ClientDataTest(NamedPipeEndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            byte[] result = new byte[_ClientHello.Length * 2];

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    endpoint,
                    () => $"ClientDataTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeReader reader = ctx.Transport.Input;

                                ReadResult readResult = await reader.ReadAsync();

                                ReadOnlySequence<byte> buffer = readResult.Buffer;
                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                await next(ctx);
                            }
                        )
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseNamedPipe
                (
                    () => $"ClientDataTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeWriter writer = ctx.Transport.Output;

                                Memory<byte> buffer = writer.GetMemory(_ClientHello.Length * 2);
                                MemoryMarshal.AsBytes<char>(_ClientHello).CopyTo(buffer.Span);

                                writer.Advance(_ClientHello.Length * 2);

                                await writer.FlushAsync();

                                await next(ctx);
                            }
                        )
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True
            (
                new ReadOnlySpan<byte>(result)
                    .SequenceEqual(MemoryMarshal.AsBytes<char>(_ClientHello))
            );
        }
    }
}
