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

            Task serverTask = CreatServerBuilder
            (
                this.ServiceProvider,
                endPoint,
                serverClientCompleted
            )
                .BuildSingleClient();

            Task clientTask = CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted
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
            TaskCompletionSource[] clientContinue = new TaskCompletionSource[maxClient];
            int currentClientIndex = 0;

            Task serverTask = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientCount);
                                clientContinue[--index].SetResult();
                                return Task.CompletedTask;
                            }
                        )
                )
                .BuildMultiClient(cts.Token);

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseSocket()
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientIndex);
                                return clientContinue[--index].Task;
                            }
                        )
                )
                .BuildClientFactory();

            for (int i = 0; i < maxClient; i++)
            {
                clientContinue[i] = new TaskCompletionSource();
            }

            for (int i = 0; i < maxClient; i++)
            {
                clients[i] = clientFactory.RunClientAsync(endpoint);
            }

            //await all clients (doing nothing)
            await Task.WhenAll(clients);

            //check if all clients have connected
            Assert.Equal(maxClient, currentClientCount);

            //shutdown the server
            cts.Cancel();

            //await the server
            await serverTask;

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
            Task[] clients = new Task[maxClient];
            TaskCompletionSource[] clientContinue = new TaskCompletionSource[maxClient];
            int currentClientIndex = 0;

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientCount);
                                clientContinue[--index].SetResult();
                                return Task.CompletedTask;
                            }
                        )
                )
                .BuildServer();

            ClientFactory clientFactory = new ClientBuilder(this.ServiceProvider)
                .UseSocket()
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                int index = Interlocked.Increment(ref currentClientIndex);
                                return clientContinue[--index].Task;
                            }
                        )
                )
                .BuildClientFactory();

            for (int i = 0; i < maxClient; i++)
            {
                clientContinue[i] = new TaskCompletionSource();
            }

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
                Assert.Equal(maxClient, currentClientCount);
            }
        }
    }
}
