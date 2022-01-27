using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Timers;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace ThePlague.Networking.Sockets
{
    //TODO: bandwidth throttling (with feature)
    public sealed partial class SocketConnection
        : ConnectionContext,
            IMeasuredDuplexPipe,
            IConnectionIdFeature,
            IConnectionTransportFeature,
            IConnectionItemsFeature,
            IConnectionLifetimeFeature,
            IConnectionEndPointFeature,
            IDisposable
    {
        /// <summary>
        /// When possible, determines how the pipe first reached a close state
        /// </summary>
        public PipeShutdownKind ShutdownKind
            => (PipeShutdownKind)Thread.VolatileRead
        (
            ref this._socketShutdownKind
        );

        /// <summary>
        /// When the ShutdownKind relates to a socket error, may contain the socket error code
        /// </summary>
        public SocketError SocketError { get; private set; }

        /// <summary>
        /// Connection for receiving data
        /// </summary>
        public PipeReader Input => this._input;

        /// <summary>
        /// Connection for sending data
        /// </summary>
        public PipeWriter Output => this._output;

        /// <summary>
        /// The underlying socket for this connection
        /// </summary>
        public Socket Socket { get; }

        public long SendSpeed
            => Interlocked.Read(ref this._sendSpeedInBytes);
        long IMeasuredDuplexPipe.BytesSentPerSecond => this.SendSpeed;

        public long ReceiveSpeed
            => Interlocked.Read(ref this._receiveSpeedInBytes);
        long IMeasuredDuplexPipe.BytesReceivedPerSecond => this.ReceiveSpeed;

        public override string ConnectionId { get; set; }
        //DO NOT USE INTERNALLY
        public override IDuplexPipe Transport { get; set; }
        public override IFeatureCollection Features { get; }
        public override IDictionary<object, object> Items { get; set; }

        private int _socketShutdownKind;

        private readonly CancellationTokenSource _connectionClosedTokenSource;
        private readonly Pipe _sendToSocket;
        private readonly Pipe _receiveFromSocket;
        private readonly PipeReader _input;
        private readonly PipeWriter _output;

        // TODO: flagify and fully implement
#pragma warning disable CS0414, CS0649, IDE0044, IDE0051, IDE0052
        private volatile bool _sendAborted, _receiveAborted;
#pragma warning restore CS0414, CS0649, IDE0044, IDE0051, IDE0052

        //values used for speed calculation
        private long _previousTotalBytesSent;
        private long _previousTotalBytesReceived;
        private long _receiveSpeedInBytes;
        private long _sendSpeedInBytes;

        //receive and send "thread"
        private static readonly Action<object> _DoReceiveAsync = DoReceiveAsync;
        private static readonly Action<object> _DoSendAsync = DoSendAsync;

        private static readonly System.Timers.Timer _BandwidthTimer;

        static SocketConnection()
        {
            _BandwidthTimer = new System.Timers.Timer(1000);
            _BandwidthTimer.Start();
        }

        private SocketConnection
        (
            Socket socket,
            PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions,
            SocketConnectionOptions socketConnectionOptions,
            string name = null
        )
        {
            if(string.IsNullOrWhiteSpace(name))
            {
                name = this.GetType().Name;
            }
            this.ConnectionId = name.Trim();

            if(sendPipeOptions == null)
            {
                sendPipeOptions = PipeOptions.Default;
            }

            if(receivePipeOptions == null)
            {
                receivePipeOptions = PipeOptions.Default;
            }

            if(socket is null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            this.Socket = socket;
            this.SocketConnectionOptions = socketConnectionOptions;
            this._sendToSocket = new Pipe(sendPipeOptions);
            this._receiveFromSocket = new Pipe(receivePipeOptions);
            this._receiveOptions = receivePipeOptions;
            this._sendOptions = sendPipeOptions;
            this._connectionClosedTokenSource = new CancellationTokenSource();

            this._input = new WrappedReader
            (
                this._receiveFromSocket.Reader,
                this
            );
            this._output = new WrappedWriter
            (
                this._sendToSocket.Writer,
                this
            );

            this.Features = new FeatureCollection();
            this.Items = new ConnectionItems();

            //set features
            this.Features.Set<IMeasuredDuplexPipe>(this);
            this.Features.Set<IConnectionIdFeature>(this);
            this.Features.Set<IConnectionTransportFeature>(this);
            this.Features.Set<IConnectionItemsFeature>(this);
            this.Features.Set<IConnectionLifetimeFeature>(this);
            this.Features.Set<IConnectionEndPointFeature>(this);

            //set transport pipe
            this.Transport = this;
            this.ConnectionClosed = this._connectionClosedTokenSource.Token;

            this._previousTotalBytesSent = 0;
            this._previousTotalBytesReceived = 0;
            this._receiveSpeedInBytes = 0;
            this._sendSpeedInBytes = 0;

            _BandwidthTimer.Elapsed += this.OnBandwidthEvent;

            sendPipeOptions.ReaderScheduler.Schedule(_DoSendAsync, this);
            receivePipeOptions.ReaderScheduler.Schedule(_DoReceiveAsync, this);
        }

        private bool TrySetShutdown(PipeShutdownKind kind)
        {
            if(kind != PipeShutdownKind.None
                && Interlocked.CompareExchange
                (
                    ref this._socketShutdownKind,
                    (int)kind,
                    0
                ) == 0)
            {
                this._connectionClosedTokenSource.Cancel();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Try to signal the pipe shutdown reason as being due to an application protocol event
        /// </summary>
        /// <param name="kind">The kind of shutdown; only protocol-related reasons will succeed</param>
        /// <returns>True if successful</returns>
        public bool TrySetProtocolShutdown(PipeShutdownKind kind)
        {
            switch(kind)
            {
                case PipeShutdownKind.ProtocolExitClient:
                case PipeShutdownKind.ProtocolExitServer:
                    return this.TrySetShutdown(kind);
                default:
                    return false;
            }
        }

        private bool TrySetShutdown
        (
            PipeShutdownKind kind,
            SocketError socketError
        )
        {
            bool win = this.TrySetShutdown(kind);
            if(win)
            {
                this.SocketError = socketError;
            }
            return win;
        }

        internal void InputReaderCompleted(Exception ex)
        {
            TrySetShutdown(ex, this, PipeShutdownKind.InputReaderCompleted);
            try
            {
                this.Socket.Shutdown(SocketShutdown.Receive);
            }
            catch
            { }
        }

        internal void OutputWriterCompleted(Exception ex)
            => TrySetShutdown(ex, this, PipeShutdownKind.OutputWriterCompleted);

        public override void Abort(ConnectionAbortedException abortReason)
            => this.TrySetShutdown
            (
                PipeShutdownKind.ConnectionAborted
            );

        private void OnBandwidthEvent(object source, ElapsedEventArgs e)
        {
            long totalBytesSent = Interlocked.Read(ref this._totalBytesSent);
            Interlocked.Exchange
            (
                ref this._sendSpeedInBytes,
                totalBytesSent - this._previousTotalBytesSent
            );
            //should be only thread writing here
            this._previousTotalBytesSent = totalBytesSent;

            long totalBytesReceived
                = Interlocked.Read(ref this._totalBytesReceived);
            Interlocked.Exchange
            (
                ref this._receiveSpeedInBytes,
                totalBytesReceived - this._previousTotalBytesReceived
            );
            //should be only thread writing here
            this._previousTotalBytesReceived = totalBytesSent;
        }

        /// <summary>
        /// Release any resources held by this instance
        /// </summary>
        public void Dispose()
        {
            this.TrySetShutdown(PipeShutdownKind.PipeDisposed);
#if DEBUG
            GC.SuppressFinalize(this);
#endif
            try
            {
                this.Socket.Shutdown(SocketShutdown.Receive);
            }
            catch
            { }

            try
            {
                this.Socket.Shutdown(SocketShutdown.Send);
            }
            catch
            { }

            try
            {
                this.Socket.Close();
            }
            catch
            { }

            try
            {
                this.Socket.Dispose();
            }
            catch
            { }

            // make sure that the async operations end ... can be twitchy!
            try
            {
                this._readerArgs?.Abort();
            }
            catch
            { }

            try
            {
                this._writerArgs?.Abort();
            }
            catch
            { }

            //clean up cancellationtoken
            this._connectionClosedTokenSource.Dispose();

            _BandwidthTimer.Elapsed -= this.OnBandwidthEvent;
        }

        public override ValueTask DisposeAsync()
        {
            this.Dispose();
            return base.DisposeAsync();
        }

        /// <summary>
        /// Gets a string representation of this object
        /// </summary>
        public override string ToString() => this.ConnectionId;

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static SocketConnection Create
        (
            Socket socket,
            PipeOptions pipeOptions = null,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            string name = null
        )
            => new SocketConnection
            (
                socket,
                pipeOptions,
                pipeOptions,
                socketConnectionOptions,
                name
            );

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static SocketConnection Create
        (
            Socket socket,
            PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            string name = null
        )
            => new SocketConnection
            (
                socket,
                sendPipeOptions,
                receivePipeOptions,
                socketConnectionOptions,
                name
            );

        private static void SetDefaultSocketOptions(Socket socket)
        {
            if(socket.AddressFamily == AddressFamily.Unix)
            {
                return;
            }

            try
            {
                socket.NoDelay = true;
            }
            //not fatal
            catch
            { }

            //initialize keep alive
            if(socket.ProtocolType == ProtocolType.Tcp)
            {
                socket.SetSocketOption
                (
                    SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive,
                    true
                );

                socket.SetSocketOption
                (
                    SocketOptionLevel.Socket,
                    SocketOptionName.TcpKeepAliveTime,
                    10
                );

                socket.SetSocketOption
                (
                    SocketOptionLevel.Socket,
                    SocketOptionName.TcpKeepAliveInterval,
                    5
                );

                socket.SetSocketOption
                (
                    SocketOptionLevel.Socket,
                    SocketOptionName.TcpKeepAliveRetryCount,
                    3
                );
            }
        }

        /// <summary>
        /// Set recommended socket options for client sockets
        /// </summary>
        /// <param name="socket">The socket to set options against</param>
        public static void SetRecommendedClientOptions(Socket socket)
            => SetDefaultSocketOptions(socket);

        /// <summary>
        /// Set recommended socket options for server sockets
        /// </summary>
        /// <param name="socket">The socket to set options against</param>
        public static void SetRecommendedServerOptions(Socket socket)
            => SetDefaultSocketOptions(socket);

        private static bool TrySetShutdown
        (
            Exception ex,
            SocketConnection connection,
            PipeShutdownKind kind
        )
        {
            try
            {
                return ex is SocketException se
                    ? connection.TrySetShutdown(kind, se.SocketErrorCode)
                    : connection.TrySetShutdown(kind);
            }
            catch
            {
                return false;
            }
        }

        private static void FireAndForget(Task task)
        {
            // make sure that any exception is observed
            if(task is null)
            {
                return;
            }

            if(task.IsCompleted)
            {
                GC.KeepAlive(task.Exception);
                return;
            }

            task.ContinueWith
            (
                t => GC.KeepAlive(t.Exception),
                TaskContinuationOptions.OnlyOnFaulted
            );
        }

        private static void DoReceiveAsync(object s)
            => FireAndForget(((SocketConnection)s).DoReceiveAsync());

        private static void DoSendAsync(object s)
            => FireAndForget(((SocketConnection)s).DoSendAsync());

        private readonly PipeOptions _receiveOptions, _sendOptions;
    }
}