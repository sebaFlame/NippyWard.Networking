using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Net.NetworkInformation;
using System.Net;
using System.Buffers;
using System.Text.Encodings;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenSSL.Core;
using OpenSSL.Core.Keys;
using OpenSSL.Core.SSL;
using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.ASN1;
using ThePlague.Networking.Logging;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Tls;
using Benchmark.LegacySsl;
using System.Text;

namespace Benchmark
{
    public abstract class BaseBenchmark
    {
        internal const int _MinimumSegmentSize = 4096;
        internal const int _BufferSize = 1024 * 1024;

        protected static List<int> _UsedPorts;
        protected static StreamPipeReaderOptions _StreamPipeReaderOptions;
        protected static StreamPipeWriterOptions _StreamPipeWriterOptions;
        protected static IServiceProvider _ServiceProvider;
        protected static SocketConnectionContextListenerFactory _SocketListenerFactory;
        protected static SocketConnectionContextFactory _SocketConnectionFactory;
        protected static StreamConnectionContextListenerFactory _StreamListenerFactory;
        protected static StreamConnectionContextFactory _StreamConnectionFactory;
        protected static MemoryPool<byte> _Pool;
        internal static byte[] _Buffer;

        static BaseBenchmark()
        {
            _ServiceProvider = new ServiceCollection()
                .AddLogging
                (
                    (builder) => builder
                        .SetMinimumLevel(LogLevel.Trace)
                        .AddProvider(new FileLoggerProvider(LogWriter.Instance))
                )
                .BuildServiceProvider();

            //1MB
            _Buffer = new byte[1024 * 1024];

            //fill buffer, so no zeroes
            Span<byte> span = new Span<byte>(_Buffer);
            span.Fill(1);

            _UsedPorts = new List<int>();

            //Initialize pool
            MemoryPool<byte> pool = _Pool = new ArrayMemoryPool<byte>();

            //receive (default) pipoptions with custom pool
            PipeOptions receiveOptions = new PipeOptions
            (
                pool: pool,
                minimumSegmentSize: _MinimumSegmentSize,
                useSynchronizationContext: false
            );

            //for a fairer comparison
            //pipeoptions without send buffering
            //pipeoptions with inline send (send inline when buffer conditions are correct)
            PipeOptions sendOptions = new PipeOptions
            (
                pool: pool,
                minimumSegmentSize: _MinimumSegmentSize,
                useSynchronizationContext: false,
                resumeWriterThreshold: 1,
                pauseWriterThreshold: 1,
                writerScheduler: PipeScheduler.Inline,
                readerScheduler: PipeScheduler.Inline
            );

            _StreamPipeWriterOptions = new StreamPipeWriterOptions
            (
                pool: pool,
                minimumBufferSize: _MinimumSegmentSize
            );
            _StreamPipeReaderOptions = new StreamPipeReaderOptions
            (
                pool: pool,
                minimumReadSize: _MinimumSegmentSize,
                useZeroByteReads: true
            );

            _SocketListenerFactory = new SocketConnectionContextListenerFactory
            (
                sendOptions: sendOptions,
                receiveOptions: receiveOptions
            );
            _SocketConnectionFactory = new SocketConnectionContextFactory
            (
                sendOptions: sendOptions,
                receiveOptions: receiveOptions
            );

            _StreamListenerFactory = new StreamConnectionContextListenerFactory
            (
                _StreamPipeReaderOptions,
                _StreamPipeWriterOptions
            );
            _StreamConnectionFactory = new StreamConnectionContextFactory
            (
                _StreamPipeReaderOptions,
                _StreamPipeWriterOptions
            );
        }

        protected static IPEndPoint CreateIPEndPoint()
            => new IPEndPoint(IPAddress.Loopback, GetAvailablePort());

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

        //returns server,client
        protected static async Task<(ConnectionContext, ConnectionContext)> InitializeConnectionContext
        (
            IConnectionListenerFactory listenerFactory,
            IConnectionFactory connectionFactory,
            EndPoint endpoint
        )
        {
            //initialize listener
            IConnectionListener connectionListener = await listenerFactory.BindAsync(endpoint);

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

            return (server, client);
        }

        protected static Task<(ConnectionContext, ConnectionContext)> InitializeSocket
        (
            SocketConnectionContextListenerFactory listenerFactory,
            SocketConnectionContextFactory connectionFactory,
            EndPoint endpoint
        )
            => InitializeConnectionContext(listenerFactory, connectionFactory, endpoint);

        protected static Task<(ConnectionContext, ConnectionContext)> InitializeStream
        (
            StreamConnectionContextListenerFactory listenerFactory,
            StreamConnectionContextFactory connectionFactory,
            EndPoint endpoint
        )
            => InitializeConnectionContext(listenerFactory, connectionFactory, endpoint);

        //ensure buffersize and buffer are incrementable with each other
        protected static ConnectionDelegate InitializeSendDelegate
        (
            IServiceProvider serviceProvider,
            int bufferSize,
            byte[] buffer
        )
        {
            return new ConnectionBuilder(serviceProvider)
                .Use
                (
                    (next) =>
                    async (ConnectionContext ctx) =>
                    {
                        PipeWriter writer = ctx.Transport.Output;
                        ValueTask<FlushResult> flushTask;
                        FlushResult flushResult;
                        Memory<byte> b;

                        for (int i = 0; i < buffer.Length; i += bufferSize)
                        {
                            b = writer.GetMemory(bufferSize);

                            new Memory<byte>(buffer, i, bufferSize).CopyTo(b);

                            writer.Advance(bufferSize);

                            flushTask = writer.FlushAsync();

                            if (!flushTask.IsCompletedSuccessfully)
                            {
                                flushResult = await flushTask;
                            }
                            else
                            {
                                flushResult = flushTask.Result;
                            }

                            if (flushResult.IsCompleted)
                            {
                                break;
                            }
                        }

                        await next(ctx);
                    }
                )
                .Build();
        }

        protected static ConnectionDelegate InitializeReceiveDelegate
        (
            IServiceProvider serviceProvider,
            int bufferSize
        )
        {
            return new ConnectionBuilder(serviceProvider)
                .Use
                (
                    (next) =>
                    async (ConnectionContext ctx) =>
                    {
                        PipeReader reader = ctx.Transport.Input;
                        int read = 0;
                        ValueTask<ReadResult> readTask;
                        ReadResult readResult;
                        ReadOnlySequence<byte> buffer;

                        while (read < bufferSize)
                        {
                            readTask = reader.ReadAsync();

                            if (!readTask.IsCompletedSuccessfully)
                            {
                                readResult = await readTask;
                            }
                            else
                            {
                                readResult = readTask.Result;
                            }

                            buffer = readResult.Buffer;

                            if (readResult.IsCompleted
                                && buffer.IsEmpty)
                            {
                                break;
                            }

                            read += (int)buffer.Length;

                            reader.AdvanceTo(buffer.End);
                        }

                        await next(ctx);
                    }
                )
                .Build();
        }
    }
}
