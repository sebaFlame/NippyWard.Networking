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
    public abstract class BaseSocketTests
    {
        public static IEnumerable<object[]> GetEndPoints() => new object[][]
        {
            new object[] { CreateIPEndPoint() },
            new object[] { CreateUnixDomainSocketEndPoint() }
        };

        public static IEnumerable<object[]> GetUnixDomainSocketEndPoint() => new object[][]
        {
            new object[] { CreateUnixDomainSocketEndPoint() }
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
            TaskCompletionSource serverTcs,
            Func<string> createName
        )
        {
            return new ServerBuilder(serviceProvider)
                .UseBlockingSendSocket(endpoint, createName)
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
                .UseBlockingSendSocket(createName)
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

        internal static Task CreateClientTask
        (
            Client client,
            TaskCompletionSource tcs
        )
        {
            return Task.Run
            (
                async () =>
                {
                    await using (client)
                    {
                        await client.StartAsync();

                        await tcs.Task;
                    }
                }
            );
        }
    }
}