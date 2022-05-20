using System;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Threading;

using Xunit;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using OpenSSL.Core.SSL;
using OpenSSL.Core.X509;
using OpenSSL.Core.Keys;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Connections.Middleware;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Tls;

namespace ThePlague.Networking.Tests
{
    /* TODO
     * clean disposal (when aborted) */
    [Collection("logging")]
    public class SocketTlsTests : BaseSocketDataTests, IDisposable
    {
        private X509Certificate ServerCertificate => this._tlsState.ServerCertificate;
        private PrivateKey ServerKey => this.ServerCertificate.PublicKey;

        private X509Certificate ClientCertificate => this._tlsState.ClientCertificate;
        private PrivateKey ClientKey => this.ClientCertificate.PublicKey;

        private X509Certificate CACertificate => this._tlsState.CACertificate;

        private TlsState _tlsState;

        public SocketTlsTests(ServicesState serviceState, ITestOutputHelper testOutputHelper)
            : base(serviceState, testOutputHelper)
        {
            this._tlsState = new TlsState();
        }

        public void Dispose()
        {
            this._tlsState.Dispose();
        }

        protected override ClientBuilder ConfigureClient(ClientBuilder clientBuilder)
            => clientBuilder.ConfigureConnection
            (
                c => c.UseClientTls()
            );

        protected override ServerBuilder ConfigureServer(ServerBuilder serverBuilder)
            => serverBuilder.ConfigureConnection
            (
                c => c.UseServerTls(this.ServerCertificate, this.ServerKey)
            );

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ServerRenegotiateTest(EndPoint endpoint)
        {
            //Force TLS1.2 to enforce a real renegotiation 

            int serverClientIndex = 0;
            int clientIndex = 0;
            string nameSuffix = $"{endpoint}";

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket
                (
                    endpoint,
                    () => $"ServerRenegotiateTest_server_{serverClientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c
                        .UseServerTls(this.ServerCertificate, this.ServerKey, SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeReader reader = ctx.Transport.Input;

                                //start "reading"
                                ValueTask<ReadResult> serverReadThread = reader.ReadAsync();
                                ReadResult readResult;

                                ITlsHandshakeFeature? handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>();
                                if(handShakeFeature is not null)
                                {
                                    await handShakeFeature.RenegotiateAsync();
                                }

                                //a read should not have bubbled up
                                Assert.False(serverReadThread.IsCompleted);

                                //renegotiation completed, send close
                                await ctx.Transport.Output.CompleteAsync();

                                //await on close from client
                                readResult = await serverReadThread;
                                Assert.True(readResult.IsCompleted);

                                //stop "reading"
                                await ctx.Transport.Input.CompleteAsync();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseSocket
                (
                    () => $"ServerRenegotiateTest_client_{clientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c
                        .UseClientTls(SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeReader reader = ctx.Transport.Input;

                                //await read
                                ReadResult readResult = await reader.ReadAsync();

                                //the only read that completes should be the close from server
                                Assert.True(readResult.IsCompleted);

                                //confirm close to server
                                await ctx.Transport.Output.CompleteAsync();

                                //stop "reading"
                                await ctx.Transport.Input.CompleteAsync();
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task DuplexRenegotiateTest(EndPoint endpoint, int maxBufferSize, int testSize)
        {
            //Force TLS1.2 to enforce a real renegotiation 

            int serverBytesSent = 0, serverBytesReceived = 0;
            int clientBytesSent = 0, clientBytesReceived = 0;

            Task<int> serverSender, clientSender, serverReceiver, clientReceiver;

            byte[] serverSent = new byte[testSize];
            byte[] serverReceived = new byte[testSize];
            byte[] clientSent = new byte[testSize];
            byte[] clientReceived = new byte[testSize];

            int serverClientIndex = 0;
            int clientIndex = 0;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket
                (
                    endpoint,
                    () => $"DuplexRenegotiateTest_server_{serverClientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.UseServerTls(this.ServerCertificate, this.ServerKey, SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                IMeasuredDuplexPipe pipeFeature = ctx.Features.Get<IMeasuredDuplexPipe>()!;
                                ITlsHandshakeFeature handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>()!;
                                PipeReader reader = ctx.Transport.Input;

                                serverReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    reader,
                                    testSize,
                                    serverReceived,
                                    this._logger,
                                    true
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

                                //wait on atleast 10 buffer sizes to start renegotiate
                                //this should be at most at about 10% of entire test (when 16k buffers)
                                while(pipeFeature.TotalBytesSent < maxBufferSize * 10)
                                {
                                    Thread.Sleep(1);
                                }

                                //ensure it ignores synchronizationcontext
                                //because it could return from the write thread (serverSender)
                                await handShakeFeature.RenegotiateAsync().ConfigureAwait(false);

                                //wait until sender completes
                                serverBytesSent = await serverSender;

                                //initiate close from server, this should end
                                //client read thread
                                await ctx.Transport.Output.CompleteAsync();

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
                                    await ctx.Transport.Input.CompleteAsync();
                                }
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseSocket
                (
                    () => $"DuplexRenegotiateTest_client_{clientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.UseClientTls(SslProtocol.Tls12)
                        .Use
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
                                await ctx.Transport.Output.CompleteAsync();

                                //verify close
                                ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                                Assert.True(readResult.IsCompleted);
                                Assert.Equal(0, readResult.Buffer.Length);

                                await ctx.Transport.Input.CompleteAsync();
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
