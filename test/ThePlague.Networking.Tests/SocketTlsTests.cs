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

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
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
                .UseSocket(() => "client")
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

            byte[] serverSent = new byte[testSize];
            byte[] clientSent = new byte[testSize];
            byte[] serverReceived = new byte[testSize];
            byte[] clientReceived = new byte[testSize];

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
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
                                    ctx.Transport.Input,
                                    testSize,
                                    serverReceived,
                                    true
                                );

                                Task<int> serverSender = RandomDataSender
                                (
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    serverSent,
                                    true
                                );

                                ITlsHandshakeFeature? handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>();
                                if (handShakeFeature is not null)
                                {
                                    await handShakeFeature.RenegotiateAsync();
                                }

                                //continue sending/receiving data for atleast 100 milliseconds
                                await Task.Delay(100);

                                //complete sending and send close to client
                                ctx.Transport.Output.Complete();

                                //sender should stop
                                await Task.WhenAll(serverSender, serverReceiver);

                                //stop reading
                                ctx.Transport.Input.Complete();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .UseSocket(() => "client")
                .ConfigureConnection
                (
                    (c) =>
                        c.UseClientTls(SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                Task<int> clientSender = RandomDataSender
                                (
                                    ctx.Transport.Output,
                                    maxBufferSize,
                                    testSize,
                                    clientSent,
                                    true
                                );

                                Task<int> clientReceiver = RandomDataReceiver
                                (
                                    ctx.Transport.Input,
                                    testSize,
                                    clientReceived,
                                    true
                                );

                                //when close requested from server, this one will finish first
                                await clientReceiver;

                                //confirm close to server
                                ctx.Transport.Output.Complete();
                                await clientSender;

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
