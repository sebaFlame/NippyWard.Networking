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

using OpenSSL.Core;
using OpenSSL.Core.Keys;
using OpenSSL.Core.SSL;
using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.ASN1;
using ThePlague.Networking.Connections;
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
        protected static MemoryPool<byte> _Pool;
        protected static PipeOptions _PipeOptions;
        protected static StreamPipeReaderOptions _StreamPipeReaderOptions;
        protected static StreamPipeWriterOptions _StreamPipeWriterOptions;
        protected static IServiceProvider _ServiceProvider;
        protected static SocketConnectionContextListenerFactory _SocketListenerFactory;
        protected static SocketConnectionContextFactory _SocketConnectionFactory;
        protected static StreamConnectionContextListenerFactory _StreamListenerFactory;
        protected static StreamConnectionContextFactory _StreamConnectionFactory;

        private OpenSSL.Core.X509.X509Certificate _openSslCertificate;
        private PrivateKey _openSslKey;
        private X509Certificate2 _certificate;
        private ConnectionContext _socketServerClient, _openSslServer, _streamServer, _legacySslServer,
            _socketClientClient, _openSslClient, _streamClient, _legacySslClient;
        private TaskCompletionSource _legacyClientTcs, _legacyServerTcs;
        private ConnectionDelegate _legacyClientDelegate, _legacyServerDelegate;
        private TaskCompletionSource _openSslClientTcs, _OpenSslServerTcs;
        private ConnectionDelegate _openSslClientDelegate, _openSslServerDelegate;

        static BaseBenchmark()
        {
            _ServiceProvider = new ServiceCollection()
                .BuildServiceProvider();

            _UsedPorts = new List<int>();

            //pre-allocate pool
            _Pool = MemoryPool<byte>.Shared;
            List<IMemoryOwner<byte>> rented = new List<IMemoryOwner<byte>>();

            //4 times buffer size should be enough
            for (int i = 0; i < _BufferSize * 4; i += _MinimumSegmentSize)
            {
                rented.Add(_Pool.Rent(_MinimumSegmentSize));
            }

            foreach (IMemoryOwner<byte> m in rented)
            {
                m.Dispose();
            }

            //pipeoptions for direct send/receive without buffering
            _PipeOptions = new PipeOptions
            (
                resumeWriterThreshold: 1,
                pauseWriterThreshold: 1,
                minimumSegmentSize: _MinimumSegmentSize,
                pool: _Pool
            );

            _StreamPipeWriterOptions = new StreamPipeWriterOptions
            (
                pool: _Pool,
                minimumBufferSize: _MinimumSegmentSize
            );
            _StreamPipeReaderOptions = new StreamPipeReaderOptions
            (
                pool: _Pool,
                useZeroByteReads: true
            );

            _SocketListenerFactory = new SocketConnectionContextListenerFactory
            (
                sendOptions: _PipeOptions,
                receiveOptions: _PipeOptions
            );
            _SocketConnectionFactory = new SocketConnectionContextFactory
            (
                sendOptions: _PipeOptions,
                receiveOptions: _PipeOptions
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

        //ensure bufferSize is a multiple of 1024
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
