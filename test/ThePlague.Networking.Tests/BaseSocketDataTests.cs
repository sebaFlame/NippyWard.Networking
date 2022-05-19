using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Collections.Generic;

using Xunit;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;
using Xunit.Sdk;

namespace ThePlague.Networking.Tests
{
    /*
     * https://docs.microsoft.com/en-us/windows/win32/winsock/graceful-shutdown-linger-options-and-socket-closure-2
     */
    public abstract class BaseSocketDataTests : BaseSocketTests
    {
        private const string _ServerHello = "Hello Client";
        private const string _ClientHello = "Hello Server";

        private ITestOutputHelper _testOutputHelper;
        protected readonly ILogger _logger;

        public BaseSocketDataTests(ServicesState serviceState, ITestOutputHelper testOutputHelper)
            : base(serviceState)
        {
            this._testOutputHelper = testOutputHelper;
            this._logger = this.ServiceProvider.CreateLogger<BaseSocketDataTests>();
        }

        protected abstract ClientBuilder ConfigureClient(ClientBuilder clientBuilder);
        protected abstract ServerBuilder ConfigureServer(ServerBuilder serverBuilder);

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ServerDataTest(EndPoint endpoint)
        {
            byte[] result = new byte[_ServerHello.Length * 2];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            endpoint,
                            () => $"{namePrefix}_ServerDataTest_server_{serverClientIndex++}_{nameSuffix}"
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
                                ReadResult readResult;

                                Memory<byte> buffer = writer.GetMemory(_ServerHello.Length * 2);
                                MemoryMarshal.AsBytes<char>(_ServerHello).CopyTo(buffer.Span);

                                writer.Advance(_ServerHello.Length * 2);

                                await writer.FlushAsync();

                                //send close to client
                                ctx.Transport.Output.Complete();

                                //await close completion
                                readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);

                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            () => $"{namePrefix}_ServerDataTest_client_{clientIndex++}_{nameSuffix}"
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
                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                //await close from server
                                readResult = await reader.ReadAsync();
                                Assert.True(readResult.IsCompleted);

                                //send close to server
                                ctx.Transport.Output.Complete();
                                ctx.Transport.Input.Complete();
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
        [MemberData(nameof(GetEndPoint))]
        public async Task ClientDataTest(EndPoint endpoint)
        {
            byte[] result = new byte[_ClientHello.Length * 2];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            endpoint,
                            () => $"{namePrefix}_ClientDataTest_server_{serverClientIndex++}_{nameSuffix}"
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
                                buffer.CopyTo(result);
                                reader.AdvanceTo(buffer.End);

                                //await close from server
                                readResult = await reader.ReadAsync();
                                Assert.True(readResult.IsCompleted);

                                //send close to server
                                ctx.Transport.Output.Complete();

                                //close receive
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            () => $"{namePrefix}_ClientDataTest_client_{clientIndex++}_{nameSuffix}"
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
                                ReadResult readResult;

                                Memory<byte> buffer = writer.GetMemory(_ClientHello.Length * 2);
                                MemoryMarshal.AsBytes<char>(_ClientHello).CopyTo(buffer.Span);

                                writer.Advance(_ClientHello.Length * 2);

                                await writer.FlushAsync();

                                //send close to server
                                ctx.Transport.Output.Complete();

                                //await close from server
                                readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);

                                //close receive
                                ctx.Transport.Input.Complete();
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

        protected static async Task<int> RandomDataSender
        (
            string connectionId,
            PipeWriter writer,
            int maxBufferSize,
            int testSize,
            Memory<byte> sent,
            ILogger logger,
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
                if(!sendContinuous 
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
                    logger.TraceLog(connectionId, "buffer acquired");

                    writeMemory
                        .Slice(0, concreteLength)
                        .Span
                        .Fill(unchecked(index++));

                    //only advance computed size, should always be >= 0
                    //because the loop will end after this call
                    writer.Advance(concreteLength);
                    logger.TraceLog(connectionId, $"buffer advanced ({concreteLength})");

                    //copy data before flush
                    //else it might get replaced with incorrect data
                    if (!sent.IsEmpty
                        && adjustedLength > 0)
                    {
                        writeMemory
                            .Slice(0, adjustedLength)
                            .CopyTo(sent.Slice(bytesSent));
                    }

                    bytesSent += concreteLength;

                    logger.TraceLog(connectionId, $"flushing {concreteLength} of {index}");

                    logger.TraceLog(connectionId, "flush initiated");
                    flushResultTask = writer.FlushAsync(cancellationToken);

                    if (!flushResultTask.IsCompletedSuccessfully)
                    {
                        flushResult = await flushResultTask;
                        logger.TraceLog(connectionId, "async flush");
                    }
                    else
                    {
                        flushResult = flushResultTask.Result;
                        logger.TraceLog(connectionId, "sync async flush");
                    }
                }
                catch(OperationCanceledException)
                {
                    logger.TraceLog(connectionId, "writer canceled");
                    return bytesSent;
                }

                if (flushResult.IsCompleted
                    || flushResult.IsCanceled)
                {
                    logger.TraceLog(connectionId, "writer completed/canceled");
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
            ILogger logger,
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

            while(receiveContinuous
                || bytesReceived < testSize)
            {
                try
                {
                    //do not pass cancellation. end should come from peer
                    logger.TraceLog(connectionId, "read initiated");
                    readResultTask = pipeReader.ReadAsync(cancellationToken);

                    if (!readResultTask.IsCompletedSuccessfully)
                    {
                        readResult = await readResultTask;
                        logger.TraceLog(connectionId, "async read");
                    }
                    else
                    {
                        readResult = readResultTask.Result;
                        logger.TraceLog(connectionId, "sync async read");
                    }
                }
                catch(OperationCanceledException)
                {
                    logger.TraceLog(connectionId, "reader canceled");

                    if (pipeReader.TryRead(out readResult))
                    {
                        logger.TraceLog(connectionId, "more data after cancellation");

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
                    if(buffer.IsEmpty)
                    {
                        logger.TraceLog(connectionId, "reader completed/canceled");
                        break;
                    }
                    else
                    {
                        logger.TraceLog(connectionId, "reader completed/canceled with buffer, continueing");
                    }
                }
            }

            return bytesReceived;
        }

        private static void ComputeBytesReceivedAndAppend
        (
            string connectionId,
            int testSize,
            bool receiveContinuous,
            ref int bytesReceived,
            in ReadOnlySequence<byte> buffer,
            Memory<byte> received,
            ILogger logger
        )
        {
            int length;

            foreach (ReadOnlyMemory<byte> memory in buffer)
            {
                if (memory.IsEmpty)
                {
                    continue;
                }

                logger.TraceLog(connectionId, $"received {string.Join(',', memory.ToArray())}");

                length = memory.Length;

                if ((bytesReceived + length) > testSize)
                {
                    length = testSize - bytesReceived;
                }

                if(!receiveContinuous
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
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task ServerWriteCloseRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
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
                        .UseBlockingSendSocket
                        (
                            endpoint,
                            () => $"{namePrefix}_ServerWriteCloseRandomDataTest_server_{serverClientIndex++}_{nameSuffix}"
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

                                //close writer and send close to client
                                ctx.Transport.Output.Complete();

                                //await close from client
                                ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);
                                Assert.Equal(0, readResult.Buffer.Length);

                                //close reader
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            () => $"{namePrefix}_ServerWriteCloseRandomDataTest_client_{clientIndex++}_{nameSuffix}"
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

                                //done reading, should not block
                                //close got received during RandomDataReceiver
                                //ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                                //Assert.True(readResult.IsCompleted);
                                //Assert.Equal(0, readResult.Buffer.Length);

                                ctx.Transport.Output.Complete();
                                ctx.Transport.Input.Complete();
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
        public async Task ClientReadCloseRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;
            byte[] sent = new byte[testSize];
            byte[] received = new byte[testSize];
            Task<int> clientSender;

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            endpoint,
                            () => $"{namePrefix}_ClientReadCloseRandomDataTest_server_{serverClientIndex++}_{nameSuffix}"
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
                                ReadResult readResult;
                                ReadOnlySequence<byte> buffer;
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

                                //notify client to close by completing sender (which is doing nothing)
                                ctx.Transport.Output.Complete();

                                //wait on close confirmation from client
                                //and ignore rest of data
                                while(true)
                                {
                                    //ignore rest of data
                                    readResult = await reader.ReadAsync();
                                    buffer = readResult.Buffer;
                                    reader.AdvanceTo(buffer.End);

                                    if(readResult.IsCompleted
                                        && buffer.IsEmpty)
                                    {
                                        break;
                                    }
                                }

                                Assert.True(readResult.IsCompleted);

                                //verify close
                                readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);
                                Assert.Equal(0, readResult.Buffer.Length);

                                //done reading
                                reader.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            () => $"{namePrefix}_ClientReadCloseRandomDataTest_client_{clientIndex++}_{nameSuffix}"
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
                                ReadResult readResult = default;

                                CancellationTokenSource cts = new CancellationTokenSource();

                                clientSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    sent,
                                    this._logger,
                                    true,
                                    cts.Token
                                );

                                //wait until server sender sends shutdown
                                readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);
                                Assert.Equal(0, readResult.Buffer.Length);

                                //complete sender
                                cts.Cancel();
                                bytesSent = await clientSender;
                                ctx.Transport.Output.Complete();
                                cts.Dispose();

                                //due to close receievd from server through ReadAsync
                                ctx.Transport.Input.Complete();
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
        public async Task DuplexRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int serverBytesSent = 0, serverBytesReceived = 0;
            int clientBytesSent = 0, clientBytesReceived = 0;

            byte[] serverSent = new byte[testSize];
            byte[] serverReceived = new byte[testSize];
            byte[] clientSent = new byte[testSize];
            byte[] clientReceived = new byte[testSize];

            Task<int> clientSender, serverReceiver, clientReceiver;

            int serverClientIndex = 0;
            int clientIndex = 0;
            string namePrefix = this.GetType().Name;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            endpoint,
                            () => $"{namePrefix}_DuplexRandomDataTest_server_{serverClientIndex++}_{nameSuffix}"
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
                                    true
                                );

                                serverBytesSent = await RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    serverSent,
                                    this._logger,
                                    false
                                );

                                //initiate close from server, this should end
                                //client read thread
                                ctx.Transport.Output.Complete();

                                try
                                {
                                    //server receiver will end through client close
                                    serverBytesReceived = await serverReceiver;

                                    //verify close
                                    ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                                    Assert.True(readResult.IsCompleted);
                                    Assert.Equal(0, readResult.Buffer.Length);
                                }
                                finally
                                {
                                    ctx.Transport.Input.Complete();
                                }
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = this.ConfigureClient
                (
                    new ClientBuilder(this.ServiceProvider)
                        .UseBlockingSendSocket
                        (
                            () => $"{namePrefix}_DuplexRandomDataTest_client_{clientIndex++}_{nameSuffix}"
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

                                //acknowledge close to server
                                ctx.Transport.Output.Complete();

                                //verify close
                                ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);
                                Assert.Equal(0, readResult.Buffer.Length);

                                ctx.Transport.Input.Complete();
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
