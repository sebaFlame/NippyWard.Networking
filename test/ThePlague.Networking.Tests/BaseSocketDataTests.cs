using System;
using System.Linq;
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
        private ILogger _logger;

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

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseSocket(endpoint)
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
                        .UseSocket()
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

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseSocket(endpoint)
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
                        .UseSocket()
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
            PipeWriter writer,
            int maxBufferSize,
            int testSize,
            byte[] sent,
            bool sendContinuous = false
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

                try
                {
                    //fill buffer with random data
                    writeMemory = writer.GetMemory(randomSize);

                    //worth about 1/4 CPU time of test method (!!!)
                    OpenSSL.Core.Interop.Random.PseudoBytes(writeMemory.Span);

                    //only advance computed size, should always be >= 0
                    //because the loop will end after this call
                    writer.Advance(sendContinuous ? randomSize : adjustedSize);

                    flushResultTask = writer.FlushAsync();

                    if (!flushResultTask.IsCompletedSuccessfully)
                    {
                        flushResult = await flushResultTask;
                    }
                    else
                    {
                        flushResult = flushResultTask.Result;
                    }
                }
                catch(Exception ex)
                {
                    break;
                }

                if (adjustedSize > 0)
                {
                    writeMemory.Slice(0, adjustedSize).CopyTo(new Memory<byte>(sent, bytesSent, adjustedSize));
                }
                
                bytesSent += (sendContinuous ? randomSize : adjustedSize);

                if(flushResult.IsCompleted)
                {
                    break;
                }
            }

            return bytesSent;
        }

        protected static async Task<int> RandomDataReceiver
        (
            PipeReader pipeReader,
            int testSize,
            byte[] received,
            bool receiveContinuous = true
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
                //do not pass cancellation. end should come from peer
                try
                {
                    readResultTask = pipeReader.ReadAsync();

                    if (!readResultTask.IsCompletedSuccessfully)
                    {
                        readResult = await readResultTask;
                    }
                    else
                    {
                        readResult = readResultTask.Result;
                    }
                }
                catch(Exception ex)
                {
                    break;
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

                    if (length > 0)
                    {
                        memory.Slice(0, length).CopyTo(new Memory<byte>(received, bytesReceived, length));
                    }

                    bytesReceived += (receiveContinuous ? memory.Length : length);
                }

                pipeReader.AdvanceTo(readResult.Buffer.End);

                if (readResult.IsCompleted)
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
            byte[] sent = new byte[testSize];
            byte[] received = new byte[testSize];

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseSocket(endpoint)
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
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    sent
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
                        .UseSocket(() => "client")
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
                                    ctx.Transport.Input,
                                    testSize,
                                    received
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

            //TODO: random failures
            /*
            Assert.True(sent.SequenceEqual(received));
            */
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task ClientReadCloseRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int bytesSent = 0, bytesReceived = 0;
            byte[] sent = new byte[testSize];
            byte[] received = new byte[testSize];
            Task<int> clientSender;

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseSocket(endpoint)
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
                                    reader,
                                    testSize,
                                    received,
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
                        .UseSocket(() => "client")
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

                                clientSender = RandomDataSender
                                (
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    sent,
                                    true
                                );

                                try
                                {
                                    //wait until server sender sends shutdown
                                    readResult = await ctx.Transport.Input.ReadAsync();
                                }
                                catch
                                { }

                                Assert.True(readResult.IsCompleted);

                                //complete sender
                                ctx.Transport.Output.Complete();
                                bytesSent = await clientSender;

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

            //TODO: random failures
            /*
            Assert.True(sent.SequenceEqual(received));
            */
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task DuplexRandomDataTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            int serverBytesSent = 0, serverBytesReceived = 0;
            int clientBytesSent = 0, clientBytesReceived = 0;
            byte[] serverSent = new byte[testSize];
            byte[] clientSent = new byte[testSize];
            byte[] serverReceived = new byte[testSize];
            byte[] clientReceived = new byte[testSize];

            Task<int> serverSender, serverReceiver, clientSender, clientReceiver;

            Task serverTask = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                        .UseSocket(endpoint)
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
                                    ctx.Transport.Input,
                                    testSize,
                                    serverReceived,
                                    true
                                );

                                serverSender = RandomDataSender
                                (
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    serverSent,
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
                        .UseSocket(() => "client")
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
                                    ctx.Transport.Input,
                                    testSize,
                                    clientReceived,
                                    true
                                );

                                clientSender = RandomDataSender
                                (
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    clientSent,
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

            /* TODO: random failures
            Assert.True(serverSent.SequenceEqual(clientReceived));
            */

            Assert.Equal(testSize, clientBytesSent);
            Assert.Equal(clientBytesSent, serverBytesReceived);

            /* TODO: random failures
            Assert.True(clientSent.SequenceEqual(serverReceived));
            */
        }
    }
}
