using System;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Tests
{
    public class ServerBuilderTests : BaseTestConnectionContextTests
    {
        public ServerBuilderTests(ServicesState serviceState)
            : base(serviceState)
        { }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ServerTaskSingleClientTest(EndPoint endPoint)
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
                () => $"ServerTaskSingleClientTest_server_{serverClientIndex++}_{endPoint}"

            )
                .BuildSingleClient();

            Task clientTask = CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted,
                () => $"ServerTaskSingleClientTest_client_{clientIndex++}_{endPoint}"
            )
                .Build(endPoint);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task CancellableServerTaskMultiClientTest(EndPoint endpoint)
        {
            int maxClient = 10;
            int serverCount = 0;
            int clientCount = 0;

            CancellationTokenSource cts = new CancellationTokenSource();
            TaskCompletionSource[] tcs = new TaskCompletionSource[maxClient];
            Task[] clients = new Task[maxClient];

            int serverClientIndex = 0;
            int clientIndex = 0;

            for (int i = 0; i < maxClient; i++)
            {
                tcs[i] = new TaskCompletionSource();
            }

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseTestConnection
                (
                    endpoint,
                    () => $"CancellableServerTaskMultiClientTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(10)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref serverCount);
                                TaskCompletionSource t = tcs[--index];

                                CancellationTokenRegistration reg = ctx.ConnectionClosed.UnsafeRegister((tcs) => ((TaskCompletionSource)tcs).SetCanceled(), t);
                                try
                                {
                                    await t.Task;
                                }
                                finally
                                {
                                    reg.Dispose();
                                }

                                await next(ctx);
                            }
                        )
                )
                .BuildMultiClient(cts.Token);

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseTestConnection
                (
                    () => $"CancellableServerTaskMultiClientTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref clientCount);
                                tcs[--index].SetResult();
                                return next(ctx);
                            }
                        )
                )
                .BuildClientFactory();


            for (int i = 0; i < maxClient; i++)
            {
                clients[i] = clientFactory.RunClientAsync(endpoint);
            }

            //await all clients (doing nothing)
            await Task.WhenAll(clients);

            //check if all clients connected
            Assert.Equal(maxClient, clientCount);

            //shutdown the server with a timeout to allow connections to gracefully close
            //as there is no way to await the clients when using a server as Task only
            cts.CancelAfter(100);

            //await the server
            await serverTask;

            //check if all clients have connected to server
            Assert.Equal(maxClient, serverCount);

            //ceck if the server shut down cleanly
            Assert.True(serverTask.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task DisposableServerMultiClientTest(EndPoint endpoint)
        {
            int maxClient = 10;
            int serverCount = 0;
            int clientCount = 0;

            CancellationTokenSource cts = new CancellationTokenSource();
            TaskCompletionSource[] tcs = new TaskCompletionSource[maxClient];
            Task[] clients = new Task[maxClient];

            int serverClientIndex = 0;
            int clientIndex = 0;

            for (int i = 0; i < maxClient; i++)
            {
                tcs[i] = new TaskCompletionSource();
            }

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseTestConnection
                (
                    endpoint,
                    () => $"DisposableServerMultiClientTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureMaxClients(10)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref serverCount);
                                TaskCompletionSource t = tcs[--index];

                                CancellationTokenRegistration reg = ctx.ConnectionClosed.UnsafeRegister((tcs) => ((TaskCompletionSource)tcs!).SetCanceled(), t);
                                try
                                {
                                    await t.Task;
                                }
                                finally
                                {
                                    reg.Dispose();
                                }

                                await next(ctx);
                            }
                        )
                )
                .BuildServer();

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseTestConnection
                (
                    () => $"DisposableServerMultiClientTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref clientCount);
                                tcs[--index].SetResult();
                                return next(ctx);
                            }
                        )
                )
                .BuildClientFactory();

            //use IAsyncDisposable
            await using (server)
            {
                await server.StartAsync();

                for (int i = 0; i < maxClient; i++)
                {
                    clients[i] = clientFactory.RunClientAsync(endpoint);
                }

                //await all clients (doing nothing)
                await Task.WhenAll(clients);

                //check if all clients have connected
                Assert.Equal(maxClient, clientCount);

                //await all server connections
                await server.Connections;

                //check if all clients have connected to server
                Assert.Equal(maxClient, serverCount);

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }
    }
}
