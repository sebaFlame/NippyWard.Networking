using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Security.Cryptography;

using Xunit;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;

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
        [MemberData(nameof(GetUnixDomainSocketEndPoint))]
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
        [MemberData(nameof(GetUnixDomainSocketEndPoint))]
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
            ILogger logger,
            bool sendContinuous = false,
            CancellationToken cancellationToken = default
        )
        {
            int bytesSent = 0;
            int randomSize, adjustedSize;
            Memory<byte> writeMemory;
            ValueTask<FlushResult> flushResultTask;
            FlushResult flushResult;

            //ensure it does not go through synchronous (and blocks)
            await Task.Yield();

            while (sendContinuous
                || bytesSent < testSize)
            {
                //ensure it can handle zero byte writes
                randomSize = RandomNumberGenerator.GetInt32(4, maxBufferSize);
                adjustedSize = randomSize;

                if ((bytesSent + randomSize) > testSize)
                {
                    adjustedSize = testSize - bytesSent;
                }

                //don't try sending 0 bytes
                if(!sendContinuous 
                    && adjustedSize <= 0)
                {
                    break;
                }

                try
                {
                    //fill buffer with random data
                    writeMemory = writer.GetMemory(randomSize);
                    logger.TraceLog(connectionId, "buffer acquired");

                    //worth about 1/4 CPU time of test method (!!!)
                    OpenSSL.Core.Interop.Random.PseudoBytes(writeMemory.Span);

                    //only advance computed size, should always be >= 0
                    //because the loop will end after this call
                    writer.Advance(sendContinuous ? randomSize : adjustedSize);
                    logger.TraceLog(connectionId, $"buffer advanced ({(sendContinuous ? randomSize : adjustedSize)})");

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
                    return bytesSent;
                }
                
                bytesSent += (sendContinuous ? randomSize : adjustedSize);

                if(flushResult.IsCompleted
                    || flushResult.IsCanceled)
                {
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
            int length = 0;

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
                    return bytesReceived;
                }

                foreach(ReadOnlyMemory<byte> memory in readResult.Buffer)
                {
                    if(memory.IsEmpty)
                    {
                        continue;
                    }

                    length = memory.Length;

                    if ((bytesReceived + length) > testSize)
                    {
                        length = testSize - bytesReceived;
                    }

                    bytesReceived += (receiveContinuous ? memory.Length : length);
                }

                pipeReader.AdvanceTo(readResult.Buffer.End);
                logger.TraceLog(connectionId, "reader advanced");

                if (readResult.IsCompleted
                    || readResult.IsCanceled)
                {
                    break;
                }
            }

            return bytesReceived;
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task ServerWriteCloseRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;

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
                            async(ConnectionContext ctx) =>
                            {
                                ReadResult readResult = default;

                                bytesSent = await RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    this._logger
                                );

                                //close writer and send close to client
                                ctx.Transport.Output.Complete();

                                //await close from client
                                try
                                {
                                    readResult = await ctx.Transport.Input.ReadAsync();
                                }
                                catch
                                { }

                                Assert.True(readResult.IsCompleted);

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
                                    this._logger
                                );

                                //close sender and send close to server
                                ctx.Transport.Output.Complete();

                                //done reading
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, bytesSent);
            Assert.Equal(bytesSent, bytesReceived);
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task ClientReadCloseRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;
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
                                PipeReader reader = ctx.Transport.Input;

                                bytesReceived = await RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    reader,
                                    testSize,
                                    this._logger,
                                    false
                                );

                                //notify client to close by completing sender (which is doing nothing)
                                ctx.Transport.Output.Complete();

                                //wait on close confirmation from client
                                while(true)
                                {
                                    try
                                    {
                                        //ignore rest of data
                                        readResult = await reader.ReadAsync();
                                        reader.AdvanceTo(readResult.Buffer.End);

                                        if(readResult.IsCompleted)
                                        {
                                            break;
                                        }
                                    }
                                    catch
                                    { }
                                }

                                Assert.True(readResult.IsCompleted);

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
                                    this._logger,
                                    true,
                                    cts.Token
                                );

                                //wait until server sender sends shutdown
                                readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);

                                //complete sender
                                cts.Cancel();
                                bytesSent = await clientSender;
                                ctx.Transport.Output.Complete();
                                cts.Dispose();

                                //also complete input (which should already be completed)
                                //due to close receievd from server through ReadAsync
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, bytesReceived);
            Assert.True(bytesSent >= bytesReceived);
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task DuplexRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int serverBytesSent = 0, serverBytesReceived = 0;
            int clientBytesSent = 0, clientBytesReceived = 0;

            Task<int> serverSender, serverReceiver, clientSender, clientReceiver;

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
                                    this._logger,
                                    true
                                );

                                serverSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    this._logger,
                                    false
                                );

                                //server sender will end when testSize reached
                                await serverSender;

                                //initiate close from server, this should end
                                //client read thread
                                ctx.Transport.Output.Complete();

                                //server receiver will end through client close
                                await serverReceiver;
                                
                                serverBytesReceived = serverReceiver.Result;
                                serverBytesSent = serverSender.Result;

                                //stop reading
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
                                    this._logger,
                                    true
                                );

                                clientSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    this._logger,
                                    false
                                );

                                //client receiver will end through server close
                                //client sender will end when testSize reached
                                await Task.WhenAll(clientReceiver, clientSender);

                                clientBytesReceived = clientReceiver.Result;
                                clientBytesSent = clientSender.Result;

                                //complete close to server
                                ctx.Transport.Output.Complete();

                                //stop reading
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.Equal(testSize, serverBytesSent);
            Assert.Equal(serverBytesSent, clientBytesReceived);

            Assert.Equal(testSize, clientBytesSent);
            Assert.Equal(clientBytesSent, serverBytesReceived);
        }
    }
}
