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
using System.IO.Pipelines;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Transports.Pipes;

namespace ThePlague.Networking.Tests
{
    public abstract class BaseTests : IClassFixture<ServicesState>
    {
        //DO NOT use unix domain sockets on windows
        //weird issues using Unix Domain Sockets on windows
        //eg socket shutdown not being sent to peer
        public static IEnumerable<object[]> GetEndPoints() => new object[][]
        {
            new object[] { CreateIPEndPoint() },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateNamedPipeEndPoint() }
        };

        //public static IEnumerable<object[]> GetEndPoint() => new object[][]
        //{
        //    new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateIPEndPoint() }
        //};

        public static IEnumerable<object[]> GetEndPointAnd1MBTestSize() => new object[][]
        {
            new object[] { CreateIPEndPoint(), 1024, 1024 * 1024 },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateNamedPipeEndPoint(), 1024, 1024 * 1024 },
            new object[] { CreateIPEndPoint(), 1024 * 4, 1024 * 1024 },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateNamedPipeEndPoint(), 1024 * 4, 1024 * 1024 },
            new object[] { CreateIPEndPoint(), 1024 * 16, 1024 * 1024 },
            new object[] { OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateNamedPipeEndPoint(), 1024 * 16, 1024 * 1024 },
        };

        internal IServiceProvider ServiceProvider => this._servicesState.ServiceProvider;

        private ServicesState _servicesState;
        private static List<int> _UsedPorts;
        private static int _SocketIndex;
        private static int _PipeIndex;

        static BaseTests()
        {
            _UsedPorts = new List<int>();
            _SocketIndex = 0;
        }

        public BaseTests(ServicesState serviceState)
        {
            this._servicesState = serviceState;
        }

        protected abstract ClientBuilder ConfigureClient(ClientBuilder clientBuilder);
        protected abstract ServerBuilder ConfigureServer(ServerBuilder serverBuilder);

        internal static IPEndPoint CreateIPEndPoint()
            => new IPEndPoint(IPAddress.Loopback, GetAvailablePort());

        internal static UnixDomainSocketEndPoint CreateUnixDomainSocketEndPoint()
            => new UnixDomainSocketEndPoint(GetUnixDomainSocketName(Interlocked.Increment(ref _SocketIndex)));

        internal static NamedPipeEndPoint CreateNamedPipeEndPoint()
            => new NamedPipeEndPoint(GetNamedPipeName(Interlocked.Increment(ref _PipeIndex)));

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

        private static string GetNamedPipeName(int index)
            => $"test_{index}";

        /*
         * https://docs.microsoft.com/en-us/windows/win32/winsock/graceful-shutdown-linger-options-and-socket-closure-2
         */

        //configure delegate to initialize graceful close
        internal static IConnectionBuilder ConfigureCloseInitializer(IConnectionBuilder connectionBuilder)
            => connectionBuilder.Use
            (
                next =>
                async (ConnectionContext ctx) =>
                {
                    try
                    {
                        //initialize read, or it might complete before you start
                        //reading
                        ValueTask<ReadResult> readTask = ctx.Transport.Input.ReadAsync();

                        //close connection
                        await ctx.Transport.Output.CompleteAsync();

                        //await confirmation close from peer
                        ReadResult readResult;
                        if(readTask.IsCompleted)
                        {
                            readResult = readTask.Result;
                        }
                        else
                        {
                            readResult = await readTask;
                        }
                        
                        Assert.True(readResult.IsCompleted);
                        Assert.True(readResult.Buffer.IsEmpty);
                    }
                    finally
                    {
                        await ctx.Transport.Input.CompleteAsync();
                    }

                    await next(ctx);
                }
            );

        //configure delegate to listen for graceful close
        internal static IConnectionBuilder ConfigureCloseListener(IConnectionBuilder connectionBuilder)
            => connectionBuilder.Use
            (
                next =>
                async (ConnectionContext ctx) =>
                {
                    try
                    {
                        //await connection close
                        ReadResult readResult = await ctx.Transport.Input.ReadAsync();
                        Assert.True(readResult.IsCompleted);
                        Assert.True(readResult.Buffer.IsEmpty);
                    }
                    finally
                    {
                        //confirm close
                        await ctx.Transport.Output.CompleteAsync();
                        await ctx.Transport.Input.CompleteAsync();
                    }

                    await next(ctx);
                }
            );

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Connect_And_Server_Close(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                    .ConfigureEndpoint
                    (
                        endpoint,
                        () => $"Connect_And_Server_Close_server_{serverClientIndex++}_{endpoint}"
                    )
                    .ConfigureMaxClients(1)
                )
                .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                .BuildServer();

            await using (server)
            {
                await server.StartAsync();

                Client client = await this.ConfigureClient
                    (
                        new ClientBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"Connect_And_Server_Close_client_{clientIndex++}_{endpoint}"   
                        )
                    )
                    .ConfigureConnection((c) => ConfigureCloseListener(c))
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }

        [Theory]
        [MemberData(nameof(GetEndPoints))]
        public async Task Connect_And_Client_Close(EndPoint endpoint)
        {
            int serverClientIndex = 0;
            int clientIndex = 0;

            Server server = this.ConfigureServer
                (
                    new ServerBuilder(this.ServiceProvider)
                    .ConfigureEndpoint
                    (
                        endpoint,
                        () => $"Connect_And_Client_Close_server_{serverClientIndex++}_{endpoint}"
                    )
                    .ConfigureMaxClients(1)
                )
                .ConfigureConnection((c) => ConfigureCloseListener(c))
                .BuildServer();

            await using (server)
            {
                await server.StartAsync();

                Client client = await this.ConfigureClient
                    (
                        new ClientBuilder(this.ServiceProvider)
                        .ConfigureEndpoint
                        (
                            endpoint,
                            () => $"Connect_And_Client_Close_client_{clientIndex++}_{endpoint}"
                        )
                    )
                    .ConfigureConnection((c) => ConfigureCloseInitializer(c))
                    .BuildClient(endpoint);

                await using (client)
                {
                    await client.RunAsync();
                }

                //ensure RunAsync ends, before calling shutdown in disposal
                await server.RunAsync();
            }
        }
    }
}