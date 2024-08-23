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
using NippyWard.OpenSSL.SSL;
using NippyWard.OpenSSL.X509;
using NippyWard.OpenSSL.Keys;

using NippyWard.Networking.Connections;
using NippyWard.Networking.Connections.Middleware;
using NippyWard.Networking.Transports;
using NippyWard.Networking.Transports.Sockets;
using NippyWard.Networking.Tls;

namespace NippyWard.Networking.Tests
{
    /* TODO
     * clean disposal (when aborted) */
    public class TlsTests : BaseDataTests, IDisposable
    {
        private X509Certificate ServerCertificate => this._tlsState.ServerCertificate;
        private PrivateKey ServerKey => this.ServerCertificate.PublicKey;

        private X509Certificate ClientCertificate => this._tlsState.ClientCertificate;
        private PrivateKey ClientKey => this.ClientCertificate.PublicKey;

        private X509Certificate CACertificate => this._tlsState.CACertificate;

        private TlsState _tlsState;

        public TlsTests(ServicesState serviceState, ITestOutputHelper testOutputHelper)
            : base(serviceState, testOutputHelper)
        {
            this._tlsState = new TlsState();
        }

        public void Dispose()
        {
            this._tlsState.Dispose();
        }

        protected override ClientBuilder ConfigureClient(ClientBuilder clientBuilder)
            => clientBuilder
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                try
                                {
                                    await next(ctx);
                                }
                                catch(TlsShutdownException)
                                { }
                            }
                        )
                )
                .ConfigureConnection
                (
                    c => c.UseClientTls()
                );

        protected override ServerBuilder ConfigureServer(ServerBuilder serverBuilder)
            => serverBuilder
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                try
                                {
                                    await next(ctx);
                                }
                                catch (TlsShutdownException)
                                { }
                            }
                        )
                )
                .ConfigureConnection
                (
                    c => c.UseServerTls(this.ServerCertificate, this.ServerKey)
                );

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Server_Init_Renegotiate(EndPoint endpoint)
        {
            //Force TLS1.2 to enforce a real renegotiation 

            int serverClientIndex = 0;
            int clientIndex = 0;
            string nameSuffix = $"{endpoint}";

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Server_Init_Renegotiate_server_{serverClientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
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

                                ITlsHandshakeFeature? handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>();
                                await handShakeFeature!.RenegotiateAsync();

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Server_Init_Renegotiate_client_{clientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
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

                                //await read to process renegotiation and shutdown
                                ReadResult readResult = await reader.ReadAsync();

                                Assert.True(readResult.IsCompleted);

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);
        }

        [Theory]
        [MemberData(nameof(GetEndPointAnd1MBTestSize))]
        public async Task Duplex_Data_Server_Init_Renegotiate(EndPoint endpoint, int maxBufferSize, int testSize)
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
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Duplex_Data_Server_Init_Renegotiate_server_{serverClientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
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

                                await handShakeFeature.InitializeRenegotiateAsync();

                                serverReceiver = RandomDataReceiver
                                (
                                    ctx.ConnectionId,
                                    reader,
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

                                //wait until sender completes
                                serverBytesSent = await serverSender;
                                serverBytesReceived = await serverReceiver;
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Duplex_Data_Server_Init_Renegotiate_client_{clientIndex++}_{nameSuffix}"
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
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

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Client_Init_Shutdown(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;
            TaskCompletionSource server_authenticated = new TaskCompletionSource();

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Client_Init_Shutdown_server_{serverClientIndex++}"
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
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
                                ValueTask<ReadResult> readTask = reader.ReadAsync();

                                server_authenticated.SetResult();

                                ReadResult readResult = await readTask;

                                Assert.True(readResult.IsCompleted);

                                await next(ctx);
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Client_Init_Shutdown_client_{clientIndex++}"
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .ConfigureConnection
                (
                    (c) =>
                        c.UseClientTls(SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                await server_authenticated.Task;

                                PipeReader reader = ctx.Transport.Input;
                                ITlsHandshakeFeature handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>()!;

                                await handShakeFeature.ShutdownAsync();

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Server_Init_Shutdown(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            TaskCompletionSource client_authenticated = new TaskCompletionSource();

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Server_Init_Shutdown_server_{serverClientIndex++}"
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .ConfigureConnection
                (
                    (c) =>
                        c.UseServerTls(this.ServerCertificate, this.ServerKey, SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                await client_authenticated.Task;

                                PipeReader reader = ctx.Transport.Input;
                                ITlsHandshakeFeature handShakeFeature = ctx.Features.Get<ITlsHandshakeFeature>()!;

                                await next(ctx);

                                await handShakeFeature.ShutdownAsync();
                            }
                        )
                )
                .BuildSingleClient();

            Task clientTask = new ClientBuilder(this.ServiceProvider)
                .ConfigureEndpoint
                (
                    endpoint,
                    () => $"Server_Init_Shutdown_client_{clientIndex++}"
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
                .ConfigureConnection
                (
                    (c) =>
                        c.UseClientTls(SslProtocol.Tls12)
                        .Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                PipeReader reader = ctx.Transport.Input;
                                ValueTask<ReadResult> readTask = reader.ReadAsync();

                                client_authenticated.SetResult();

                                ReadResult readResult = await readTask;

                                Assert.True(readResult.IsCompleted);

                                await next(ctx);
                            }
                        )
                )
                .Build(endpoint);

            await Task.WhenAll(serverTask, clientTask);
        }
    }
}
