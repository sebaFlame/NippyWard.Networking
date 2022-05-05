﻿using System;
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
using ThePlague.Networking.Connections.Middleware;

namespace ThePlague.Networking.Tests
{
    public class ClientBuilderTests : BaseSocketTests
    {
        public ClientBuilderTests(ServicesState serviceState)
            : base(serviceState)
        { }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task ClientTaskTest(EndPoint endPoint)
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
        public async Task ClientTest(EndPoint endPoint)
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

            Client client = await CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted
            )
                .BuildClient(endPoint);

            Task clientTask = CreateClientTask(client, clientCompleted);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task ClientFactoryTest(EndPoint endPoint)
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

            ClientFactory clientFactory = CreateClientBuilder
            (
                this.ServiceProvider,
                clientCompleted
            )
                .BuildClientFactory();

            Client client = await clientFactory.ConnectAsync(endPoint);

            Task clientTask = CreateClientTask(client, clientCompleted);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverClientCompleted.Task.IsCompletedSuccessfully);
            Assert.True(clientCompleted.Task.IsCompletedSuccessfully);
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task CancellableClientTaskTest(EndPoint endpoint)
        {
            CancellationTokenSource clientCts = new CancellationTokenSource();
            TaskCompletionSource clientConnected = new TaskCompletionSource();
            TaskCompletionSource clientTerminal = new TaskCompletionSource();

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
                .ConfigureSingleConnection()
                .ConfigureConnection
                (
                    //ensure both ends stay open
                    (c) => c.UseTerminal()
                )
                .BuildServer();

            TestConnectionLifetime connectionLifetime = new TestConnectionLifetime(clientTerminal);

            await using(server)
            {
                await server.StartAsync();

                Task clientTask = new ClientBuilder(this.ServiceProvider)
                    .UseSocket()
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                (ConnectionContext ctx) =>
                                {
                                    ctx.Features.Set<IConnectionLifetimeNotificationFeature>(connectionLifetime);
                                    clientConnected.SetResult();
                                    return next(ctx);
                                }
                            )
                    )
                    .Build(endpoint, clientCts.Token);

                //ensure client is connected
                await clientConnected.Task;

                //cancel the client
                clientCts.Cancel();

                //check if no errors are thrown after clean shutdown using IConnectionLifetimeNotificationFeature
                await clientTask;
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task DisposableClientTest(EndPoint endpoint)
        {
            CancellationTokenSource clientCts = new CancellationTokenSource();
            TaskCompletionSource clientConnected = new TaskCompletionSource();
            TaskCompletionSource clientTerminal = new TaskCompletionSource();

            Server server = new ServerBuilder(this.ServiceProvider)
                .UseSocket(endpoint)
                .ConfigureSingleConnection()
                .ConfigureConnection
                (
                    //ensure both ends stay open
                    (c) => c.UseTerminal()
                )
                .BuildServer();

            TestConnectionLifetime connectionLifetime = new TestConnectionLifetime(clientTerminal);

            await using (server)
            {
                await server.StartAsync();

                Client client = await new ClientBuilder(this.ServiceProvider)
                    .UseSocket()
                    .ConfigureConnection
                    (
                        (c) =>
                            c.Use
                            (
                                next =>
                                (ConnectionContext ctx) =>
                                {
                                    ctx.Features.Set<IConnectionLifetimeNotificationFeature>(connectionLifetime);
                                    clientConnected.SetResult();
                                    return next(ctx);
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
            }
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
