using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSSL.Core.Keys;
using OpenSSL.Core.ASN1;
using OpenSSL.Core.X509;
using OpenSSL.Core.SSL;

using ThePlague.Networking.Logging;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Transports.Pipes;
using ThePlague.Networking.Tls;

namespace Bandwidth
{
    public class Program
    {
        static Memory<byte> _Buffer;
        private static IList<int> _UsedPorts;
        private static int _SocketIndex;
        private static int _PipeIndex;

        static Program()
        {
            //initialize 16K buffer for bandwidth tests
            _Buffer = new byte[1024 * 16];

            _UsedPorts = new List<int>();
            _SocketIndex = 0;
        }

        static async Task Main(string[] args)
        {
            IServiceProvider serviceProvider = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Debug);
                    builder.AddConsole();
                })
                .BuildServiceProvider();

            Console.WriteLine("- Bandwidth tests -");
            Console.WriteLine("client & server simultaneously");
            Console.WriteLine("  1.     TCP IP Sockets");
            Console.WriteLine("  2.     TLS over TCP IP Sockets");
            Console.WriteLine($"  3.     {(OperatingSystem.IsLinux() ? "Unix Domain Sockets" : "Named Pipes")}");
            Console.WriteLine($"  4.     TLS over {(OperatingSystem.IsLinux() ? "Unix Domain Sockets" : "Named Pipes")}");
            Console.WriteLine("  Other. exit");

            ConsoleKeyInfo keyInfo;
            while(true)
            {
                keyInfo = Console.ReadKey();

                switch (keyInfo.Key)
                {
                    case ConsoleKey.D1:
                        await IpSocketBandwidth(Console.Out, serviceProvider);
                        continue;
                    case ConsoleKey.D2:
                        await IpSocketBandwidth(Console.Out, serviceProvider, true);
                        continue;
                    case ConsoleKey.D3:
                        await PipeBandwidth(Console.Out, serviceProvider);
                        continue;
                    case ConsoleKey.D4:
                        await PipeBandwidth(Console.Out, serviceProvider, true);
                        continue;
                    default:
                        return;
                }
            }
        }

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

            lock (((ICollection)_UsedPorts).SyncRoot)
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

        protected static void InitializeCertificate
        (
            out X509Certificate openSslCertificate,
            out PrivateKey openSslKey
        )
        {
            var start = DateTime.Now;
            var end = start + TimeSpan.FromMinutes(10);

            //create key
            openSslKey = new RSAKey(2048);
            openSslKey.GenerateKey();

            //create certificate
            openSslCertificate = new OpenSSL.Core.X509.X509Certificate
            (
                openSslKey,
                "localhost",
                "localhost",
                start,
                end
            );

            //self sign certificate
            openSslCertificate.SelfSign(openSslKey, DigestType.SHA256);
        }

        private static Task IpSocketBandwidth
        (
            TextWriter writer,
            IServiceProvider serviceProvider,
            bool useTls = false
        )
        {
            IConnectionListenerFactory listener = new SocketConnectionContextListenerFactory();
            IConnectionFactory factory = new SocketConnectionContextFactory();

            return StartBandwidth
            (
                writer,
                serviceProvider,
                CreateIPEndPoint(),
                listener,
                factory,
                useTls
            );
        }

        private static Task PipeBandwidth
        (
            TextWriter writer,
            IServiceProvider serviceProvider,
            bool useTls = false
        )
        {
            IConnectionListenerFactory listener = new NamedPipeConnectionListenerFactory();
            IConnectionFactory factory = new NamedPipeConnectionFactory();

            return StartBandwidth
            (
                writer,
                serviceProvider,
                OperatingSystem.IsLinux() ? CreateUnixDomainSocketEndPoint() : CreateNamedPipeEndPoint(),
                listener,
                factory,
                useTls
            );
        }

        private static async Task StartBandwidth
        (
            TextWriter writer,
            IServiceProvider serviceProvider,
            EndPoint endpoint,
            IConnectionListenerFactory connectionListenerFactory,
            IConnectionFactory connectionFactory,
            bool useTls = false
        )
        {
            //initialize listener
            IConnectionListener connectionListener = await connectionListenerFactory.BindAsync(endpoint);

            //initialize accept
            ValueTask<ConnectionContext?> serverTask = connectionListener.AcceptAsync();

            //initialize client
            ConnectionContext client = await connectionFactory.ConnectAsync(endpoint);

            //get server
            ConnectionContext? server = await serverTask;

            if (server is null)
            {
                throw new InvalidOperationException("Server could not be created");
            }

            X509Certificate cert = null;
            PrivateKey key = null;
            SslProtocol protocol = SslProtocol.Tls13;
            if(useTls)
            {
                InitializeCertificate(out cert, out key);
            }

            long sent = 0, received = 0;

            IConnectionBuilder serverBuilder = new ConnectionBuilder(serviceProvider);
            if(useTls)
            {
                serverBuilder = serverBuilder.UseServerTls(cert, key, protocol);
            }

            ConnectionDelegate serverDelegate = serverBuilder
                .Use
                (
                    (next) =>
                    async (ctx) =>
                    {
                        sent = await DoSend(ctx);

                        await next(ctx);
                    }
                )
                .Use
                (
                    (next) =>
                    async (ctx) =>
                    {
                        ValueTask<ReadResult> readTask = ctx.Transport.Input.ReadAsync();

                        await ctx.Transport.Output.CompleteAsync();

                        await readTask;

                        await ctx.Transport.Input.CompleteAsync();
                    }
                )
                .Build();

            IConnectionBuilder clientBuilder = new ConnectionBuilder(serviceProvider);
            if (useTls)
            {
                clientBuilder = clientBuilder.UseClientTls(protocol);
            }

            ConnectionDelegate clientDelegate = clientBuilder
                .Use
                (
                    (next) =>
                    async (ctx) =>
                    {
                        received = await DoReceive(ctx);
                        await next(ctx);
                    }
                )
                .Use
                (
                    (next) =>
                    async (ctx) =>
                    {
                        try
                        {
                            await ctx.Transport.Input.ReadAsync();
                        }
                        catch
                        { }

                        await ctx.Transport.Output.CompleteAsync();
                        await ctx.Transport.Input.CompleteAsync();
                    }
                )
                .Build();

            await Task.WhenAll
            (
                serverDelegate(server),
                clientDelegate(client)
            );

            Console.WriteLine();
            writer.WriteLine($"Server send bandwidth: {sent / (1024 * 1024):0.##}MB/s");
            writer.WriteLine($"Client receive bandwidth: {received / (1024 * 1024):0.##}MB/s");
        }

        private static async Task<long> DoSend
        (
            ConnectionContext ctx
        )
        {
            PipeWriter writer = ctx.Transport.Output;
            ValueTask<FlushResult> flushTask;
            FlushResult flushResult;
            Memory<byte> b;
            int bufferSize;
            long sent = 0;

            CancellationTokenSource cts = new CancellationTokenSource(1000);
            CancellationToken cancellationToken = cts.Token;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    //copy over entire buffer
                    for (int i = 0; i < _Buffer.Length; i += 1024)
                    {
                        b = writer.GetMemory(1);
                        bufferSize = b.Length;

                        if (bufferSize > _Buffer.Length - i)
                        {
                            _Buffer.Slice(i).CopyTo(b);
                        }
                        else
                        {
                            _Buffer.Slice(i, bufferSize).CopyTo(b);
                        }
                        
                        writer.Advance(bufferSize);
                    }

                    flushTask = writer.FlushAsync(cancellationToken);

                    if (!flushTask.IsCompletedSuccessfully)
                    {
                        flushResult = await flushTask;
                    }
                    else
                    {
                        flushResult = flushTask.Result;
                    }

                    sent += _Buffer.Length;

                    if (flushResult.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            { }
            finally
            {
                cts.Dispose();
            }

            return sent;
        }

        protected static async Task<long> DoReceive
        (
            ConnectionContext ctx
        )
        {
            PipeReader reader = ctx.Transport.Input;
            ValueTask<ReadResult> readTask;
            ReadResult readResult;
            ReadOnlySequence<byte> buffer;
            long received = 0;

            CancellationTokenSource cts = new CancellationTokenSource(1000);
            CancellationToken cancellationToken = cts.Token;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    readTask = reader.ReadAsync(cancellationToken);

                    if (!readTask.IsCompletedSuccessfully)
                    {
                        readResult = await readTask;
                    }
                    else
                    {
                        readResult = readTask.Result;
                    }

                    buffer = readResult.Buffer;
                    received += (int)buffer.Length;

                    if (readResult.IsCompleted
                        && buffer.IsEmpty)
                    {
                        break;
                    }

                    reader.AdvanceTo(buffer.End);
                }
            }
            catch(OperationCanceledException)
            { }
            finally
            {
                cts.Dispose();
            }

            return received;
        }
    }
}
