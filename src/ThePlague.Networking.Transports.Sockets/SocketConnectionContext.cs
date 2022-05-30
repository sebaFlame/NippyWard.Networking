using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Runtime.CompilerServices;
using System.Diagnostics;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Logging;

namespace ThePlague.Networking.Transports.Sockets
{
    //TODO: bandwidth throttling (with feature)
    public sealed partial class SocketConnectionContext
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
        public override CancellationToken ConnectionClosed { get => this._connectionClosedTokenSource.Token; set => throw new NotSupportedException(); }

        internal Task _receiveTask, _sendTask;

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

        private static readonly System.Timers.Timer _BandwidthTimer;

        static SocketConnectionContext()
        {
            _BandwidthTimer = new System.Timers.Timer(1000);
            _BandwidthTimer.Start();
        }

        private SocketConnectionContext
        (
            Socket socket,
            PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions,
            SocketConnectionOptions socketConnectionOptions,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null
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

            this.Features = new FeatureCollection(featureCollection);
            this.Items = new ConnectionItems();
            this._logger = logger;

            //set features
            this.Features.Set<IMeasuredDuplexPipe>(this);
            this.Features.Set<IConnectionIdFeature>(this);
            this.Features.Set<IConnectionTransportFeature>(this);
            this.Features.Set<IConnectionItemsFeature>(this);
            this.Features.Set<IConnectionLifetimeFeature>(this);
            this.Features.Set<IConnectionEndPointFeature>(this);

            //set transport pipe
            this.Transport = this;

            this._previousTotalBytesSent = 0;
            this._previousTotalBytesReceived = 0;
            this._receiveSpeedInBytes = 0;
            this._sendSpeedInBytes = 0;

            _BandwidthTimer.Elapsed += this.OnBandwidthEvent;

            //initialize receive/send thread
            //ensure these are on a threadpool thread
            //the _sendOptions and _receiveOptions are random and can be inline!
            //this ensures all code gets executed on a thread and NOT in this constructor
            ThreadPool.UnsafeQueueUserWorkItem<SocketConnectionContext>(DoReceiveAsync, this, false);
            ThreadPool.UnsafeQueueUserWorkItem<SocketConnectionContext>(DoSendAsync, this, false);
        }

        ~SocketConnectionContext()
        {
            this.Dispose(false);
        }

        private bool TrySetShutdown(PipeShutdownKind kind)
        {
            if (kind != PipeShutdownKind.None
                && Interlocked.CompareExchange
                (
                    ref this._socketShutdownKind,
                    (int)kind,
                    0
                ) == 0)
            {
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
        {
            this.TraceLog("abort on ConnectionContext called");

            if(this._connectionClosedTokenSource.IsCancellationRequested)
            {
                return;
            }

            this.TrySetShutdown
            (
                PipeShutdownKind.ConnectionAborted
            );

            //complete if not completed yet, so write thread can end
            //this does not override existing completion
            try
            {
                this._output.Complete(new ConnectionAbortedException(nameof(SocketConnectionContext)));
            }
            catch
            { }

            //complete if not completed yet, so read thread can end
            //this does not override existing completion
            try
            {
                this._input.Complete(new ConnectionAbortedException(nameof(SocketConnectionContext)));
            }
            catch
            { }

            this._connectionClosedTokenSource.Cancel();
        }

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

        public override async ValueTask DisposeAsync()
        {
            try
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.ConnectionAborted
                );

                //ensure connection closed
                this._connectionClosedTokenSource.Cancel();
            }
            catch
            { }

            this.Dispose();

            this.TraceLog("awaiting Receive/Send thread");

            //ensure receive/send thread ended
            //this should always happen as socket gets disposed
            //and Pipes get completed
            //discard any errors (they will be logged in each thread)
            try
            {
                await Task.WhenAll(this._receiveTask, this._sendTask);
            }
            catch
            { }

            await base.DisposeAsync();
        }

        /// <summary>
        /// Release any resources held by this instance
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
        }

        private void Dispose(bool isDisposing)
        {
            this.TraceLog("disposing");

            this.TrySetShutdown(PipeShutdownKind.PipeDisposed);

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

            //complete if not completed yet, so write thread can end
            //this does not override existing completion
            try
            {
                this._output.Complete(new ObjectDisposedException(nameof(SocketConnectionContext)));
            }
            catch
            { }

            //complete if not completed yet, so read thread can end
            //this does not override existing completion
            try
            {
                this._input.Complete(new ObjectDisposedException(nameof(SocketConnectionContext)));
            }
            catch
            { }

            try
            {
                //clean up cancellationtoken
                this._connectionClosedTokenSource.Dispose();
            }
            catch
            { }

            try
            {
                _BandwidthTimer.Elapsed -= this.OnBandwidthEvent;
            }
            catch
            { }

            if(!isDisposing)
            {
                return;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets a string representation of this object
        /// </summary>
        public override string ToString() => this.ConnectionId;

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static SocketConnectionContext Create
        (
            Socket socket,
            PipeOptions pipeOptions = null,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            IFeatureCollection serverFeatureCollection = null,
            string name = null,
            ILogger logger = null
        )
            => new SocketConnectionContext
            (
                socket,
                pipeOptions,
                pipeOptions,
                socketConnectionOptions,
                serverFeatureCollection,
                name,
                logger
            );

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static SocketConnectionContext Create
        (
            Socket socket,
            PipeOptions sendPipeOptions,
            PipeOptions receivePipeOptions,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            IFeatureCollection serverFeatureCollection = null,
            string name = null,
            ILogger logger = null
        )
            => new SocketConnectionContext
            (
                socket,
                sendPipeOptions,
                receivePipeOptions,
                socketConnectionOptions,
                serverFeatureCollection,
                name,
                logger
            );

        private static void SetDefaultSocketOptions(Socket socket)
        {
            socket.LingerState = new LingerOption(true, 10);

            if (socket.AddressFamily == AddressFamily.Unix)
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
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveTime,
                    10
                );

                socket.SetSocketOption
                (
                    SocketOptionLevel.Tcp,
                    SocketOptionName.TcpKeepAliveInterval,
                    5
                );

                socket.SetSocketOption
                (
                    SocketOptionLevel.Tcp,
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
            SocketConnectionContext connection,
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

        private static void DoReceiveAsync(SocketConnectionContext ctx)
        {
            ctx._receiveTask = ctx.DoReceiveAsync();
        }

        private static void DoSendAsync(SocketConnectionContext ctx)
        {
            ctx._sendTask = ctx.DoSendAsync();
        }

        private readonly PipeOptions _receiveOptions, _sendOptions;
        private readonly ILogger _logger;

        [Conditional("TRACELOG")]
        private void TraceLog(string message, [CallerFilePath] string file = null, [CallerMemberName] string caller = null, [CallerLineNumber] int lineNumber = 0)
        {
#if TRACELOG
            this._logger?.TraceLog(this.ConnectionId, message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");
#endif
        }
    }
}