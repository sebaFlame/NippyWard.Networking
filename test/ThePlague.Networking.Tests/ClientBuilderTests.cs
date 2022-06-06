using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

using Xunit;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Connections.Middleware;

namespace ThePlague.Networking.Tests
{
    public class ClientBuilderTests : BaseTestConnectionContextTests
    {
        public ClientBuilderTests(ServicesState serviceState)
            : base(serviceState)
        { }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ClientTaskTest(EndPoint endPoint)
        {
            TaskCompletionSource serverClientCompleted, clientCompleted;

            serverClientCompleted = new TaskCompletionSource();
            clientCompleted = new TaskCompletionSource();
            int serverClientIndex = 0;
            int clientIndex = 0;

            Task serverTask = CreatServerBuilder
            (
                this.ServiceProvider,
                endPoint,
                serverClientCompleted,
                () => $"ClientTaskTest_server_{serverClientIndex++}_{endPoint}"
            )
                .BuildSingleClient();

            Task clientTask = CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted,
                () => $"ClientTaskTest_client_{clientIndex++}_{endPoint}"
            )
                .Build(endPoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ClientTest(EndPoint endPoint)
        {
            TaskCompletionSource serverClientCompleted, clientCompleted;

            serverClientCompleted = new TaskCompletionSource();
            clientCompleted = new TaskCompletionSource();
            int serverClientIndex = 0;
            int clientIndex = 0;

            Task serverTask = CreatServerBuilder
            (
                this.ServiceProvider,
                endPoint,
                serverClientCompleted,
                () => $"ClientTest_server_{serverClientIndex++}_{endPoint}"
            )
                .BuildSingleClient();

            Client client = await CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted,
                () => $"ClientTest_client_{clientIndex++}_{endPoint}"
            )
                .BuildClient(endPoint);

            Task clientTask = CreateClientTask(client, clientCompleted);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ClientFactoryTest(EndPoint endPoint)
        {
            TaskCompletionSource serverClientCompleted, clientCompleted;

            serverClientCompleted = new TaskCompletionSource();
            clientCompleted = new TaskCompletionSource();
            int serverClientIndex = 0;
            int clientIndex = 0;

            Task serverTask = CreatServerBuilder
            (
                this.ServiceProvider,
                endPoint,
                serverClientCompleted,
                () => $"ClientFactoryTest_server_{serverClientIndex++}_{endPoint}"
            )
                .BuildSingleClient();

            ClientFactory clientFactory = CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted,
                () => $"ClientFactoryTest_client_{clientIndex++}_{endPoint}"
            )
                .BuildClientFactory();

            Client client = await clientFactory.ConnectAsync(endPoint);

            Task clientTask = CreateClientTask(client, clientCompleted);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task CancellableClientTaskTest(EndPoint endpoint)
        {
            CancellationTokenSource clientCts = new CancellationTokenSource();
            TaskCompletionSource clientConnected = new TaskCompletionSource();
            TaskCompletionSource clientTerminal = new TaskCompletionSource();
            TaskCompletionSource serverConnected = new TaskCompletionSource();

            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = CreatServerBuilder
                (
                    this.ServiceProvider,
                    endpoint,
                    serverConnected,
                    () => $"CancellableClientTaskTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(1)
                .BuildServer();

            await using(server)
            {
                await server.StartAsync();

                Task clientTask = new ClientBuilder(this.ServiceProvider)
                    .UseTestConnection
                    (
                        () => $"CancellableClientTaskTest_client_{clientIndex++}_{endpoint}"
                    )
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                async (ConnectionContext ctx) =>
                                {
                                    CancellationTokenRegistration reg = ctx.ConnectionClosed.UnsafeRegister((tcs) => ((TaskCompletionSource)tcs!).SetCanceled(), clientTerminal);

                                    try
                                    {
                                        clientConnected.SetResult();
                                        await clientTerminal.Task;
                                    }
                                    finally
                                    {
                                        reg.Dispose();
                                    }

                                    await next(ctx);
                                }
                            )
                    )
                    .Build(endpoint, clientCts.Token);

                //ensure client and server are "connected"
                await Task.WhenAll(serverConnected.Task, clientConnected.Task);

                //cancel the client
                //this initiates ctx.ConnectionClosed
                clientCts.Cancel();

                //check if the cancellation gets thrown, because it does not get caught
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clientTask);

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task DisposableClientTest(EndPoint endpoint)
        {
            CancellationTokenSource clientCts = new CancellationTokenSource();
            TaskCompletionSource clientConnected = new TaskCompletionSource();
            TaskCompletionSource clientTerminal = new TaskCompletionSource();
            TaskCompletionSource serverConnected = new TaskCompletionSource();

            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = CreatServerBuilder
                (
                    this.ServiceProvider,
                    endpoint,
                    serverConnected,
                    () => $"DisposableClientTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(1)
                .BuildServer();

            TestConnectionLifetime connectionLifetime = new TestConnectionLifetime(clientTerminal);

            await using (server)
            {
                await server.StartAsync();

                Client client = await new ClientBuilder(this.ServiceProvider)
                    .UseTestConnection
                    (
                        () => $"DisposableClientTest_client_{clientIndex++}_{endpoint}"
                    )
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                async (ConnectionContext ctx) =>
                                {
                                    ctx.Features.Set<IConnectionLifetimeNotificationFeature>(connectionLifetime);

                                    clientConnected.SetResult();

                                    await clientTerminal.Task;

                                    await next(ctx);
                                }
                            )
                    )
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.StartAsync();

                    //ensure client is connected
                    await clientConnected.Task;
                }

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }

            Assert.True(clientTerminal.Task.IsCompletedSuccessfully);
        }

        private class TestConnectionLifetime : IConnectionLifetimeNotificationFeature
        {
            public CancellationToken ConnectionClosedRequested { get; set; }

            private readonly TaskCompletionSource _tcs;

            public TestConnectionLifetime
            (
                TaskCompletionSource tcs
            )
            {
                this._tcs = tcs;
                this.ConnectionClosedRequested = CancellationToken.None;
            }

            public void RequestClose()
            {
                //then set result, so CancellationTokenSource can get disposed
                this._tcs.TrySetResult();
            }
        }
    }
}
