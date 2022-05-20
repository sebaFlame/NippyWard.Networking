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
    public abstract class BaseSocketTests : IClassFixture<ServicesState>
    {
        //DO NOT use on windows
        //weird issues using Unix Domain Sockets on windows
        //eg socket shutdown not being sent to peer
        public static IEnumerable<object[]> GetEndPoints() => new object[][]
        {
            new object[] { CreateIPEndPoint() },
            new object[] { CreateUnixDomainSocketEndPoint() }
        };

        public static IEnumerable<object[]> GetEndPoint() => new object[][]
        {
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateIPEndPoint() }
        };

        public static IEnumerable<object[]> GetEndPointAnd1MBTestSize() => new object[][]
        {
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateIPEndPoint(), 1024, 1024 * 1024 },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateIPEndPoint(), 1024 * 4, 1024 * 1024 },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateIPEndPoint(), 1024 * 16, 1024 * 1024 },
        };

        internal IServiceProvider ServiceProvider => this._servicesState.ServiceProvider;

        private ServicesState _servicesState;
        private static List<int> _UsedPorts;
        private static int _SocketIndex;

        static BaseSocketTests()
        {
            _UsedPorts = new List<int>();
            _SocketIndex = 0;
        }

        public BaseSocketTests(ServicesState serviceState)
        {
            this._servicesState = serviceState;
        }

        protected abstract ClientBuilder ConfigureClient(ClientBuilder clientBuilder);
        protected abstract ServerBuilder ConfigureServer(ServerBuilder serverBuilder);

        internal static IPEndPoint CreateIPEndPoint()
            => new IPEndPoint(IPAddress.Loopback, GetAvailablePort());

        internal static UnixDomainSocketEndPoint CreateUnixDomainSocketEndPoint()
            => new UnixDomainSocketEndPoint(GetUnixDomainSocketName(Interlocked.Increment(ref _SocketIndex)));

        private static int GetAvailablePort()
        {
            //does not run on WSL1
            TcpConnectionInformation[] tcpConnInfoArray = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            TcpConnectionInformation? info;

            lock(((ICollection)_UsedPorts).SyncRoot)
            {
                for (int i = 1025; i <= UInt16.MaxValue; i++)
                {
                    //only use each port 1 time => no possible TIMEOUT due to unclean shutdown
                    if (_UsedPorts.Contains(i))
                    {
                        continue;
                    }

                    if ((info = tcpConnInfoArray.FirstOrDefault(x => x.LocalEndPoint.Port == i)) == null
                        || info.State == TcpState.Closed)
                    {
                        _UsedPorts.Add(i);
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string GetUnixDomainSocketName(int index)
            => $"test_{index}.sock";

        internal static ServerBuilder CreatServerBuilder
        (
            IServiceProvider serviceProvider,
            EndPoint endpoint,
            Func<string> createName
        )
            => ConfigureDefaultServerClose
            (
                new ServerBuilder(serviceProvider)
                    .UseSocket(endpoint, createName)
            );

        internal static ClientBuilder CreateClientBuilder
        (
            IServiceProvider serviceProvider,
            Func<string> createName
        )
            => ConfigureDefaultServerClose
            (
                new ClientBuilder(serviceProvider)
                    .UseSocket(createName)
            );

        internal static ServerBuilder ConfigureDefaultServerClose(ServerBuilder serverBuilder)
            => serverBuilder.ConfigureConnection
            (
                (c) =>
                    c.Use
                    (
                        next =>
                        async (ConnectionContext ctx) =>
                        {
                            //close connection
                            await ctx.Transport.Output.CompleteAsync();

                            try
                            {
                                //await confirmation
                                await ctx.Transport.Input.ReadAsync();
                            }
                            finally
                            {
                                await ctx.Transport.Input.CompleteAsync();
                            }
                        }
                    )
            );

        internal static ClientBuilder ConfigureDefaultServerClose(ClientBuilder clientBuilder)
            => clientBuilder.ConfigureConnection
            (
                (c) =>
                    c.Use
                    (
                        next =>
                        async (ConnectionContext ctx) =>
                        {
                            try
                            {
                                //await connection close
                                await ctx.Transport.Input.ReadAsync();
                            }
                            finally
                            {
                                //confirm close
                                await ctx.Transport.Output.CompleteAsync();
                                await ctx.Transport.Input.CompleteAsync();
                            }
                        }
                    )
            );

        internal static async Task CreateClientTask
        (
            Client client
        )
        {
            await Task.Yield();

            await using (client)
            {
                await client.StartAsync();

                //DisposeAsync waits on the connection
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoint))]
        public async Task ConnectionTest(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            Task serverTask = this.ConfigureServer
                (
                    CreatServerBuilder
                    (
                        this.ServiceProvider,
                        endpoint,
                        () => $"ConnectionTest_server_{serverClientIndex++}_{endpoint}"
                    )
                )
                .BuildSingleClient();

            Client client = await this.ConfigureClient
                (
                    CreateClientBuilder
                    (
                        this.ServiceProvider,
                        () => $"ConnectionTest_client_{clientIndex++}_{endpoint}"
                    )
                )
                .BuildClient(endpoint);

            Task clientTask = CreateClientTask(client);

            await Task.WhenAll(serverTask, clientTask);
        }
    }
}