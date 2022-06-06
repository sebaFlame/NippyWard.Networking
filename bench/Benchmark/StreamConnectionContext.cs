using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Pipelines;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Threading;
using System.Net.Sockets;

namespace Benchmark
{
    public class StreamConnectionContext : ConnectionContext, IStreamFeature
    {
        public override string ConnectionId { get; set; }
        public override IDuplexPipe Transport { get; set; }
        public override IDictionary<object, object?> Items { get; set; }
        public override IFeatureCollection Features => this._features;
        public override CancellationToken ConnectionClosed { get => this._cts.Token; set => throw new NotSupportedException(); }
        public Stream Stream { get; set; }

        private readonly IFeatureCollection _features;
        private readonly CancellationTokenSource _cts;
        
        private readonly PipeReader _input;
        private readonly PipeWriter _output;

        private StreamPipeReaderOptions? _readerOptions;
        private StreamPipeWriterOptions? _writerOptions;

        public StreamConnectionContext
        (
            Stream stream,
            string connectionId,
            StreamPipeReaderOptions? readerOptions = null,
            StreamPipeWriterOptions? writerOptions = null
        )
        {
            this.ConnectionId = connectionId;
            this._features = new FeatureCollection();
            this.Items = new ConnectionItems();
            this._cts = new CancellationTokenSource();
            this.Stream = stream;

            this._readerOptions = readerOptions;
            this._writerOptions = writerOptions;

            this._input = PipeReader.Create(stream, this._readerOptions);
            this._output = PipeWriter.Create(stream, this._writerOptions);

            this._features.Set<IStreamFeature>(this);

            this.Transport = new DuplexPipe(this._input, this._output);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            this._cts.Cancel();

            try
            {
                this._input.Complete();
            }
            catch
            { }

            try
            {
                this._output.Complete();
            }
            catch
            { }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                this._input.Complete();
            }
            catch
            { }

            try
            {
                this._output.Complete();
            }
            catch
            { }

            try
            {
                await this.Stream.DisposeAsync();
            }
            catch
            { }
        }
    }

    public class StreamConnectionServer : IConnectionListener
    {
        public EndPoint EndPoint { get; }

        private Socket _listenerSocket;
        private Func<string> _createConnectionId;
        private StreamPipeReaderOptions? _readerOptions;
        private StreamPipeWriterOptions? _writerOptions;

        public StreamConnectionServer
        (
            Socket listenerSocket,
            Func<string> createConnectionId,
            StreamPipeReaderOptions? readerOptions = null,
            StreamPipeWriterOptions? writerOptions = null
        )
        {
            this.EndPoint = listenerSocket.LocalEndPoint!;
            this._listenerSocket = listenerSocket;
            this._createConnectionId = createConnectionId;
            this._readerOptions = readerOptions;
            this._writerOptions = writerOptions;
        }

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            Socket clientSocket = await this._listenerSocket.AcceptAsync();

            NetworkStream netStream = new NetworkStream(clientSocket);

            return new StreamConnectionContext
            (
                netStream,
                this._createConnectionId(),
                this._readerOptions,
                this._writerOptions
            );
        }

        public ValueTask DisposeAsync()
            => default;

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)

        {
            Socket socket = this._listenerSocket;
            this._listenerSocket = null;

            if (socket is not null)
            {
                try
                {
                    socket.Dispose();
                }
                catch { }
            }

            return default;
        }
    }

    public class StreamConnectionContextFactory : IConnectionFactory
    {
        private Func<string> _createConnectionId;
        private StreamPipeReaderOptions? _readerOptions;
        private StreamPipeWriterOptions? _writerOptions;

        public StreamConnectionContextFactory
        (
            StreamPipeReaderOptions? readerOptions = null,
            StreamPipeWriterOptions? writerOptions = null,
            Func<string>? createConnectionId = null
        )
        {
            if(createConnectionId is null)
            {
                this._createConnectionId = () => string.Empty;
            }
            else
            {
                this._createConnectionId = createConnectionId;
            }

            this._readerOptions = readerOptions;
            this._writerOptions = writerOptions;
        }

        public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            AddressFamily addressFamily =
                endpoint.AddressFamily == AddressFamily.Unspecified
                    ? AddressFamily.InterNetwork
                    : endpoint.AddressFamily;

            ProtocolType protocolType =
                addressFamily == AddressFamily.Unix
                    ? ProtocolType.Unspecified
                    : ProtocolType.Tcp;

            Socket socket = new Socket
            (
                addressFamily,
                SocketType.Stream,
                protocolType
            );

            await socket.ConnectAsync(endpoint);

            NetworkStream netStream = new NetworkStream(socket);

            return new StreamConnectionContext
            (
                netStream,
                this._createConnectionId(),
                this._readerOptions,
                this._writerOptions
            );
        }
    }

    public class StreamConnectionContextListenerFactory : IConnectionListenerFactory
    {
        private StreamPipeReaderOptions? _readerOptions;
        private StreamPipeWriterOptions? _writerOptions;

        private Func<string> _createConnectionId;

        public StreamConnectionContextListenerFactory
        (
            StreamPipeReaderOptions? readerOptions = null,
            StreamPipeWriterOptions? writerOptions = null,
            Func<string>? createConnectionId = null
        )
        {
            this._readerOptions = readerOptions;
            this._writerOptions = writerOptions;

            if (createConnectionId is null)
            {
                this._createConnectionId = () => string.Empty;
            }
            else
            {
                this._createConnectionId = createConnectionId;
            }
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            AddressFamily addressFamily =
                endpoint.AddressFamily == AddressFamily.Unspecified
                    ? AddressFamily.InterNetwork
                    : endpoint.AddressFamily;

            ProtocolType protocolType =
                addressFamily == AddressFamily.Unix
                    ? ProtocolType.Unspecified
                    : ProtocolType.Tcp;

            Socket listener = new Socket
            (
                addressFamily,
                SocketType.Stream,
                protocolType
            );

            listener.Bind(endpoint);
            listener.Listen(20);

            return new ValueTask<IConnectionListener>
            (
                new StreamConnectionServer
                (
                    listener,
                    this._createConnectionId,
                    this._readerOptions,
                    this._writerOptions
                )
            );
        }
    }
}

