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
        [MemberData(nameof(GetUnixDomainSocketEndPoint))]
        public async Task ServerRenegotiateTest(EndPoint endpoint)
        {
            //Force TLS1.2 to enforce a real renegotiation 

            int serverClientIndex = 0;
            int clientIndex = 0;
            string nameSuffix = $"{endpoint}";

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
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
                                ctx.Transport.Output.Complete();

                                //await on close from client
                                readResult = await serverReadThread;
                                Assert.True(readResult.IsCompleted);

                                //stop "reading"
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
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
                                ctx.Transport.Output.Complete();

                                //stop "reading"
                                ctx.Transport.Input.Complete();
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

            int serverClientIndex = 0;
            int clientIndex = 0;
            string nameSuffix = $"{endpoint}_{maxBufferSize}_{testSize}";

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
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
                                PipeReader reader = ctx.Transport.Input;

                                Task<int> serverReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Input,
                                    testSize,
                                    this._logger,
                                    true
                                );

                                CancellationTokenSource cts = new CancellationTokenSource();

                                Task<int> serverSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    this._logger,
                                    true,
                                    cts.Token
                                );

                                ITlsHandshakeFeature? handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>();
                                if (handShakeFeature is not null)
                                {
                                    await handShakeFeature.RenegotiateAsync();
                                }

                                //complete sending after 100 milliseconds of sending and send close to client
                                cts.CancelAfter(100);
                                await serverSender;
                                ctx.Transport.Output.Complete();
                                cts.Dispose();

                                //receiver should stop
                                await serverReceiver;

                                //stop reading
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
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
                                CancellationTokenSource cts = new CancellationTokenSource();

                                Task<int> clientSender = RandomDataSender
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    this._logger,
                                    true,
                                    cts.Token
                                );

                                Task<int> clientReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    ctx.Transport.Input,
                                    testSize,
                                    this._logger,
                                    true
                                );

                                //when close requested from server, this one will finish first
                                await clientReceiver;

                                //confirm close to server
                                cts.Cancel();
                                await clientSender;
                                ctx.Transport.Output.Complete();
                                cts.Dispose();

                                //stop reading
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);
        }
    }
}
