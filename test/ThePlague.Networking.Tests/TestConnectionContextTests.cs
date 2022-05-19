using System;
using System.Threading;
using System.Linq;
using System.Collections;
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
    public abstract class BaseTestConnectionContextTests : IClassFixture<ServicesState>
    {
        public static IEnumerable<object[]> GetEndPoint() => new object[][]
        {
            new object[] { new TestConnectionEndPoint() }
        };

        internal IServiceProvider ServiceProvider => this._servicesState.ServiceProvider;

        private ServicesState _servicesState;

        public BaseTestConnectionContextTests(ServicesState serviceState)
        {
            this._servicesState = serviceState;
        }

        internal static ServerBuilder CreatServerBuilder
        (
            IServiceProvider serviceProvider,
            EndPoint endpoint,
            TaskCompletionSource serverTcs,
            Func<string> createName
        )
        {
            return new ServerBuilder(serviceProvider)
                .UseTestConnection(endpoint, createName)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                serverTcs.SetResult();
                                return next(ctx);
                            }
                        )
                );
        }

        internal static ClientBuilder CreateClientBuilder
        (
            IServiceProvider serviceProvider,
            TaskCompletionSource clientTcs,
            Func<string> createName
        )
        {
            return new ClientBuilder(serviceProvider)
                .UseTestConnection(createName)
                .ConfigureConnection
                (
                    (c) =>
                        c.Use
                        (
                            next =>
                            (ConnectionContext ctx) =>
                            {
                                clientTcs.SetResult();
                                return next(ctx);
                            }
                        )
                );
        }

        internal static async Task CreateClientTask
        (
            Client client,
            TaskCompletionSource tcs
        )
        {
            await Task.Yield();

            await using (client)
            {
                await client.StartAsync();

                await tcs.Task;
            }
        }
    }
}
