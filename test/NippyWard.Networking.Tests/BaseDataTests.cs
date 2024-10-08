﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Collections.Generic;

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using System.IO.Pipelines;
using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;

using NippyWard.Networking.Connections;
using NippyWard.Networking.Transports.Sockets;
using NippyWard.Networking.Logging;
using Microsoft.AspNetCore.Connections.Features;
using NippyWard.Networking.Tls;

namespace NippyWard.Networking.Tests
{
    public abstract class BaseDataTests : BaseTests
    {
        private const string _ServerHello = "Hello Client";
        private const string _ClientHello = "Hello Server";

        private readonly ITestOutputHelper _testOutputHelper;
        protected readonly ILogger? _logger;

        public BaseDataTests(ServicesState serviceState, ITestOutputHelper testOutputHelper)
            : base(serviceState)
        {
            this._testOutputHelper = testOutputHelper;
            this._logger = this.ServiceProvider.CreateLogger<BaseDataTests>();
        }

        protected static async Task<int> RandomDataSender
        (
            string connectionId,
            PipeWriter writer,
            int maxBufferSize,
            int testSize,
            Memory<byte> sent,
            ILogger? logger,
            bool sendContinuous = false,
            CancellationToken cancellationToken = default
        )
        {
            int bytesSent = 0;
            int randomSize, adjustedLength, concreteLength;
            Memory<byte> writeMemory;
            ValueTask<FlushResult> flushResultTask;
            FlushResult flushResult;
            byte index = 0;

            //ensure it does not go through synchronous (and blocks)
            await Task.Yield();

            while (sendContinuous
                || bytesSent < testSize)
            {
                //ensure it can handle zero byte writes
                randomSize = RandomNumberGenerator.GetInt32(4, maxBufferSize);
                adjustedLength = randomSize;

                if ((bytesSent + randomSize) > testSize)
                {
                    adjustedLength = testSize - bytesSent;
                }

                //don't try sending 0 bytes
                if (!sendContinuous
                    && adjustedLength <= 0)
                {
                    break;
                }

                concreteLength = sendContinuous ? randomSize : adjustedLength;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    //fill buffer with random data
                    writeMemory = writer.GetMemory(concreteLength);
                    logger?.TraceLog(connectionId, "buffer acquired");

                    writeMemory
                        .Slice(0, concreteLength)
                        .Span
                        .Fill(unchecked(index++));

                    //only advance computed size, should always be >= 0
                    //because the loop will end after this call
                    writer.Advance(concreteLength);
                    logger?.TraceLog(connectionId, $"buffer advanced ({concreteLength})");

                    //copy data before flush
                    //else it might get replaced with incorrect data
                    if (!sent.IsEmpty
                        && adjustedLength > 0)
                    {
                        writeMemory
                            .Slice(0, adjustedLength)
                            .CopyTo(sent.Slice(bytesSent));
                    }

                    logger?.TraceLog(connectionId, $"flushing {concreteLength} of {index}");

                    logger?.TraceLog(connectionId, "flush initiated");
                    flushResultTask = writer.FlushAsync(cancellationToken);

                    bytesSent += concreteLength;

                    if (!flushResultTask.IsCompletedSuccessfully)
                    {
                        flushResult = await flushResultTask;
                        logger?.TraceLog(connectionId, "async flush");
                    }
                    else
                    {
                        flushResult = flushResultTask.Result;
                        logger?.TraceLog(connectionId, "sync async flush");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger?.TraceLog(connectionId, "writer canceled");
                    return bytesSent;
                }

                if (flushResult.IsCompleted
                    || flushResult.IsCanceled)
                {
                    logger?.TraceLog(connectionId, "writer completed/canceled");
                    break;
                }
            }

            return bytesSent;
        }

        protected static async Task<int> RandomDataReceiver
        (
            string connectionId,
            PipeReader pipeReader,
            int testSize,
            Memory<byte> received,
            ILogger? logger,
            bool receiveContinuous = true,
            CancellationToken cancellationToken = default
        )
        {
            //ensure it does not go through synchronous
            await Task.Yield();

            ValueTask<ReadResult> readResultTask;
            ReadResult readResult;
            int bytesReceived = 0;
            ReadOnlySequence<byte> buffer;

            while (receiveContinuous
                || bytesReceived < testSize)
            {
                try
                {
                    //do not pass cancellation. end should come from peer
                    logger?.TraceLog(connectionId, "read initiated");
                    readResultTask = pipeReader.ReadAsync(cancellationToken);

                    if (!readResultTask.IsCompletedSuccessfully)
                    {
                        readResult = await readResultTask;
                        logger?.TraceLog(connectionId, "async read");
                    }
                    else
                    {
                        readResult = readResultTask.Result;
                        logger?.TraceLog(connectionId, "sync async read");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger?.TraceLog(connectionId, "reader canceled");

                    if (pipeReader.TryRead(out readResult))
                    {
                        logger?.TraceLog(connectionId, "more data after cancellation");

                        buffer = readResult.Buffer;

                        ComputeBytesReceivedAndAppend
                        (
                            connectionId,
                            testSize,
                            receiveContinuous,
                            ref bytesReceived,
                            in buffer,
                            received,
                            logger
                        );

                        pipeReader.AdvanceTo(buffer.End);
                    }

                    return bytesReceived;
                }

                buffer = readResult.Buffer;

                ComputeBytesReceivedAndAppend
                (
                    connectionId,
                    testSize,
                    receiveContinuous,
                    ref bytesReceived,
                    in buffer,
                    received,
                    logger
                );

                pipeReader.AdvanceTo(buffer.End);

                if (readResult.IsCompleted
                    || readResult.IsCanceled)
                {
                    logger?.TraceLog(connectionId, "reader completed/canceled");
                    break;
                }
            }

            return bytesReceived;
        }

        private static void ComputeBytesReceivedAndAppend
        (
#pragma warning disable IDE0060 // Remove unused parameter
            string connectionId,

            int testSize,
            bool receiveContinuous,
            ref int bytesReceived,
            in ReadOnlySequence<byte> buffer,
            Memory<byte> received,
            ILogger? logger
#pragma warning restore IDE0060 // Remove unused parameter
        )
        {
            int length;

            foreach (ReadOnlyMemory<byte> memory in buffer)
            {
                if (memory.IsEmpty)
                {
                    continue;
                }

                length = memory.Length;

                if ((bytesReceived + length) > testSize)
                {
                    length = testSize - bytesReceived;
                }

                if (!receiveContinuous
                    && length <= 0)
                {
                    break;
                }

                if (!received.IsEmpty
                    && length > 0)
                {
                    memory
                        .Slice(0, length)
                        .CopyTo(received.Slice(bytesReceived));
                }

                bytesReceived += (receiveContinuous ? memory.Length : length);
            }
        }

        private static void AssertSentReceived
        (
            byte[] sent,
            byte[] received
        )
        {
            List<ValueTuple<int, byte, byte>> assertions = new List<(int, byte, byte)>();

            for (int i = 0; i < sent.Length; i++)
            {
                if (sent[i] != received[i])
                {
                    assertions.Add((i, sent[i], received[i]));
                }

                //try
                //{
                //    Assert.Equal(sent[i], received[i]);
                //}
                //catch (EqualException)
                //{
                //    throw new XunitException($"Incorrect @ {i}");
                //}
            }

            if (assertions.Count > 0)
            {
                throw new XunitException(string.Join('\n', assertions.Select(t => $"sent {t.Item2}, received {t.Item3} @ {t.Item1}")));
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Connect_And_Server_Close(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;
            TaskCompletionSource client_connected = new TaskCompletionSource();
            TaskCompletionSource server_connected = new TaskCompletionSource();

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"Connect_And_Server_Close_server_{serverClientIndex++}_{endpoint}"
                        )
                        .ConfigureMaxClients(1)
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                await client_connected.Task;
                                server_connected.SetResult();

                                await next(ctx);
                            }
                        )
                )
                .BuildServer();

            await using (server)
            {
                await server.StartAsync();

                Client client = await this.ConfigureClient
                    (
                        new ClientBuilder(this.ServiceProvider)
                            .ConfigureConnection((c) => ConfigureCloseListener(c))
                            .ConfigureEndpoint
                            (
                                endpoint,
                                () => $"Connect_And_Server_Close_client_{clientIndex++}_{endpoint}"
                            )
                    )
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                async (ConnectionContext ctx) =>
                                {
                                    client_connected.SetResult();
                                    await server_connected.Task;

                                    await next(ctx);
                                }
                            )
                    )
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Connect_And_Client_Close(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;
            TaskCompletionSource client_connected = new TaskCompletionSource();
            TaskCompletionSource server_connected = new TaskCompletionSource();

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"Connect_And_Client_Close_server_{serverClientIndex++}_{endpoint}"
                        )
                        .ConfigureMaxClients(1)
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                await client_connected.Task;
                                server_connected.SetResult();

                                await next(ctx);
                            }
                        )
                )
                .BuildServer();

            await using (server)
            {
                await server.StartAsync();

                Client client = await this.ConfigureClient
                    (
                        new ClientBuilder(this.ServiceProvider)
                            .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                            .ConfigureEndpoint
                            (
                                endpoint,
                                () => $"Connect_And_Client_Close_client_{clientIndex++}_{endpoint}"
                            )
                    )
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                async (ConnectionContext ctx) =>
                                {
                                    client_connected.SetResult();
                                    await server_connected.Task;

                                    await next(ctx);
                                }
                            )
                    )
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Server_Data_Send_And_Server_Close(EndPoint endpoint)
        {
            byte[] result = new byte[_ServerHello.Length * 2];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Server_Data_Send_And_Server_Close_server_{serverClientIndex++}_{nameSuffix}"
                        )
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
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Server_Data_Send_And_Server_Close_client_{clientIndex++}_{nameSuffix}"
                        )
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

                                //Assert.False(readResult.IsCompleted);
                                Assert.False(buffer.IsEmpty);

                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True
            (
                new ReadOnlySpan<byte>(result)
                    .SequenceEqual(MemoryMarshal.AsBytes<char>(_ServerHello))
            );
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Client_Data_Send_And_Client_Close(EndPoint endpoint)
        {
            byte[] result = new byte[_ClientHello.Length * 2];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Client_Data_Send_And_Client_Close_server_{serverClientIndex++}_{nameSuffix}"
                        )
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

                                Assert.False(buffer.IsEmpty);

                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Client_Data_Send_And_Client_Close_client_{clientIndex++}_{nameSuffix}"
                        )
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
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True
            (
                new ReadOnlySpan<byte>(result)
                    .SequenceEqual(MemoryMarshal.AsBytes<char>(_ClientHello))
            );
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task Server_Random_Data_Send_And_Server_Close(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;
            byte[] sent = new byte[testSize];
            byte[] received = new byte[testSize];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Server_Random_Data_Send_And_Server_Close_server_{serverClientIndex++}_{nameSuffix}"
                        )
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                bytesSent = await RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    sent,
                                    this._logger
                                );

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Server_Random_Data_Send_And_Server_Close_client_{clientIndex++}_{nameSuffix}"
                        )
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                bytesReceived = await RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Input,
                                    testSize,
                                    received,
                                    this._logger
                                );

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, bytesSent);
            Assert.Equal(bytesSent, bytesReceived);

            Assert.True(sent.SequenceEqual(received));
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task Client_Random_Data_Read_And_Client_Close(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;
            byte[] sent = new byte[testSize];
            byte[] received = new byte[testSize];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Client_Random_Data_Read_And_Client_Close_server_{serverClientIndex++}_{nameSuffix}"
                        )
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

                                bytesReceived = await RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    reader,
                                    testSize,
                                    received,
                                    this._logger,
                                    false
                                );

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Client_Random_Data_Read_And_Client_Close_client_{clientIndex++}_{nameSuffix}"
                        )
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                bytesSent = await RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    sent,
                                    this._logger
                                );

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, bytesReceived);
            Assert.True(bytesSent >= bytesReceived);

            Assert.True(sent.SequenceEqual(received));
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task Duplex_Random_Data(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int serverBytesSent = 0, serverBytesReceived = 0;
            int clientBytesSent = 0, clientBytesReceived = 0;

            byte[] serverSent = new byte[testSize];
            byte[] serverReceived = new byte[testSize];
            byte[] clientSent = new byte[testSize];
            byte[] clientReceived = new byte[testSize];

            Task<int> clientSender, serverSender, serverReceiver, clientReceiver;

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Duplex_Random_Data_server_{serverClientIndex++}_{nameSuffix}"
                        )
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                serverReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Input,
                                    testSize,
                                    serverReceived,
                                    this._logger,
                                    false
                                );

                                serverSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    serverSent,
                                    this._logger,
                                    false
                                );

                                await Task.WhenAll(serverReceiver, serverSender);

                                serverBytesReceived = serverReceiver.Result;
                                serverBytesSent = serverSender.Result;

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .ConfigureConnection((c) => ConfigureCloseListener(c))
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"{namePrefix}_Duplex_Random_Data_client_{clientIndex++}_{nameSuffix}"
                        )
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                clientReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Input,
                                    testSize,
                                    clientReceived,
                                    this._logger,
                                    true
                                );

                                clientSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    clientSent,
                                    this._logger,
                                    false
                                );

                                //close from server will end clientReceiver
                                //clientSender will end when testSize has been sent
                                await Task.WhenAll(clientReceiver, clientSender);

                                clientBytesReceived = clientReceiver.Result;
                                clientBytesSent = clientSender.Result;

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, serverBytesSent);
            Assert.Equal(serverBytesSent, clientBytesReceived);
            Assert.True(serverSent.SequenceEqual(clientReceived));

            Assert.Equal(testSize, clientBytesSent);
            Assert.Equal(clientBytesSent, serverBytesReceived);
            Assert.True(clientSent.SequenceEqual(serverReceived));
        }
    }
}
