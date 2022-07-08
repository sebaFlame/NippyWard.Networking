using System;
using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

using NippyWard.Networking.Connections;
using NippyWard.Networking.Transports.Proxy;

namespace NippyWard.Proxy.Tests
{
    public enum ProxyType
    {
        CLIENT,
        SERVER
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            ProxyType proxyType;

        start:
            Console.Clear();

            Console.WriteLine("[1] Client");
            Console.WriteLine("[2] Server");

            ConsoleKeyInfo info = Console.ReadKey();
            Console.WriteLine();

            switch (info.Key)
            {
                case ConsoleKey.D1:
                    proxyType = ProxyType.CLIENT;
                    break;
                case ConsoleKey.D2:
                    proxyType = ProxyType.SERVER;
                    break;
                default:
                    return;
            }

            Console.WriteLine("Enter Proxy host IP");
            IPAddress proxyAddress = GetIPAddress();

            Console.WriteLine("Enter Proxy port");
            int proxyPort = GetPort();

            Console.WriteLine("Enter Proxy user (or leave empty)");
            string? userName = Console.ReadLine();

            Console.WriteLine("Enter Proxy password (or leave empty)");
            string? password = Console.ReadLine();

            Console.WriteLine($"Enter {(proxyType == ProxyType.CLIENT ? "CONNECT" : "BIND")} address");
            IPAddress address = GetIPAddress();

            Console.WriteLine($"Enter {(proxyType == ProxyType.CLIENT ? "CONNECT" : "BIND")} port");
            int port = GetPort();

            //construct the proxy uri
            Uri proxyUri = new Uri
            (
                string.Concat
                (
                    "socks5://",
                    userName is null || password is null
                        ? ""
                        : string.Concat
                        (
                            Uri.EscapeDataString(userName),
                            ":",
                            Uri.EscapeDataString(password),
                            "@"
                        ),
                    proxyAddress.ToString(),
                    ":",
                    proxyPort.ToString()
                )
            );

            IPEndPoint remote = new IPEndPoint(address, port);

            Task task = Task.CompletedTask;

            async Task FetchRemoteEndPoint(ConnectionContext ctx)
            {
                Console.WriteLine($"Connected to {ctx.RemoteEndPoint}");

                //complete connection immediately
                await ctx.Transport.Output.CompleteAsync();
            }

            ConnectionDelegate @delegate
                = new ConnectionDelegate(FetchRemoteEndPoint);

            ConsoleColor prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;

            if (proxyType == ProxyType.CLIENT)
            {
                task = new ClientBuilder()
                    .UseProxy
                    (
                        proxyUri
                    )
                    .ConfigureConnection
                    (
                        c =>
                        c.Use
                        (
                            n => @delegate
                        )
                    )
                    .Build(remote);
            }
            else if(proxyType == ProxyType.SERVER)
            {
                Server server = new ServerBuilder()
                    .UseProxy
                    (
                        proxyUri,
                        remote
                    )
                    .ConfigureMaxClients(1)
                    .ConfigureConnection
                    (
                        c =>
                        c.Use
                        (
                            n => @delegate
                        )
                    )
                    .BuildServer();

                await server.BindAsync();

                //figure out where the server is bound to
                //e.g. to send to client
                if (server.TryGetConnectionListener
                (
                    remote,
                    out IConnectionListener? connectionListener
                ))
                {
                    Console.WriteLine($"Proxy BOUND to {connectionListener.EndPoint}");
                }

                task = server.RunAsync();
            }

            try
            {
                await task;

                //continue showing output until key press
                Console.ReadKey();
            }
            finally
            {
                Console.ForegroundColor = prev;
            }

        //loop
        goto start;
        }

        private static IPAddress GetIPAddress()
        {
            string? host;
            IPAddress? ipAddress;
            bool first = true;

            do
            {
                if(!first)
                {
                    Console.WriteLine("Please try again!");
                }

                first = false;
                host = Console.ReadLine();
            } while (string.IsNullOrEmpty(host)
                || !IPAddress.TryParse(host, out ipAddress));

            return ipAddress;
        }

        private static int GetPort()
        {
            string? prt;
            int port;
            bool first = true;

            do
            {
                if (!first)
                {
                    Console.WriteLine("Please try again!");
                }

                first = false;
                prt = Console.ReadLine();
            } while (string.IsNullOrEmpty(prt)
                || !int.TryParse(prt, out port));

            return port;
        }
    }
}