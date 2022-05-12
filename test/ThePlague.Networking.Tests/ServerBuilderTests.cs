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
using ThePlague.Networking.Transports.Sockets;

namespace ThePlague.Networking.Tests
{
    [Collection("logging")]
    public class ServerBuilderTests : BaseSocketTests
    {
        public ServerBuilderTests(ServicesState serviceState)
            : base(serviceState)
        { }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
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
        [MemberData(nameof(GetEndPoints))]
        public async Task CancellableServerTaskMultiClientTest(EndPoint endpoint)
        {
            int maxClient = 10;
            CancellationTokenSource cts = new CancellationTokenSource();
            int currentClientCount = 0;
            Task[] clients = new Task[maxClient];
            int currentClientIndex = 0;

            int serverClientIndex = 0;
            int clientIndex = 0;

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket
                (
                    endpoint,
                    () => $"CancellableServerTaskMultiClientTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientCount);

                                //close connection
                                ctx.Transport.Output.Complete();

                                //await confirmation
                                await ctx.Transport.Input.ReadAsync();
                            }
                        )
                )
                .BuildMultiClient(cts.Token);

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
                (
                    () => $"CancellableServerTaskMultiClientTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientIndex);

                                //await connection close
                                await ctx.Transport.Input.ReadAsync();

                                //confirm close
                                ctx.Transport.Output.Complete();
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
            Assert.Equal(maxClient, currentClientIndex);

            //shutdown the server with a timeout to allow connections to gracefully close
            //as there is no way to await the clients when using a server as Task only
            cts.CancelAfter(1000);

            //await the server
            await serverTask;

            //check if all clients have connected to server
            Assert.Equal(maxClient, currentClientCount);

            //ceck if the server shut down cleanly
            Assert.True(serverTask.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task DisposableServerMultiClientTest(EndPoint endpoint)
        {
            int maxClient = 10;
            CancellationTokenSource cts = new CancellationTokenSource();
            int currentClientCount = 0;
            int currentClientIndex = 0;
            Task[] clients = new Task[maxClient];

            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseSocket
                (
                    endpoint,
                    () => $"DisposableServerMultiClientTest_server_{serverClientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientCount);

                                //close connection
                                ctx.Transport.Output.Complete();

                                //await confirmation
                                await ctx.Transport.Input.ReadAsync();
                            }
                        )
                )
                .BuildServer();

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseBlockingSendSocket
                (
                    () => $"DisposableServerMultiClientTest_client_{clientIndex++}_{endpoint}"
                )
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            async (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientIndex);

                                //await connection close
                                await ctx.Transport.Input.ReadAsync();

                                //confirm close
                                ctx.Transport.Output.Complete();
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
                Assert.Equal(maxClient, currentClientIndex);

                //await all server connections
                await server.Connections;

                //check if all clients have connected to server
                Assert.Equal(maxClient, currentClientCount);
            }
        }
    }
}
