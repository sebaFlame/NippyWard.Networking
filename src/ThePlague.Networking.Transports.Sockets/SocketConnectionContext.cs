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

using ThePlague.Networking.Transports;
using ThePlague.Networking.Logging;

namespace ThePlague.Networking.Transports.Sockets
{
    //TODO: bandwidth throttling (with feature)
    public sealed partial class SocketConnectionContext : TransportConnectionContext
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

        public override PipeReader Input => this._input;
        public override PipeWriter Output => this._output;

        /// <summary>
        /// The underlying socket for this connection
        /// </summary>
        public Socket Socket { get; }

        private int _socketShutdownKind;
        private readonly WrappedReader _input;
        private readonly WrappedWriter _output;

        // TODO: flagify and fully implement
#pragma warning disable CS0414, CS0649, IDE0044, IDE0051, IDE0052
        private volatile bool _sendAborted, _receiveAborted;
#pragma warning restore CS0414, CS0649, IDE0044, IDE0051, IDE0052

        private SocketConnectionContext
        (
            Socket socket,
            Pipe sendToSocket,
            Pipe receiveFromSocket,
            PipeScheduler sendScheduler,
            PipeScheduler receiveScheduler,
            SocketConnectionOptions socketConnectionOptions,
            IFeatureCollection? featureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
            : base
            (
                  socket.LocalEndPoint!,
                  socket.RemoteEndPoint!,
                  sendToSocket,
                  receiveFromSocket,
                  sendScheduler,
                  receiveScheduler,
                  featureCollection,
                  name,
                  logger
            )
        {
            if(socket is null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            this.Socket = socket;
            this.SocketConnectionOptions = socketConnectionOptions;

            this._input = new SocketReader
            (
                receiveFromSocket.Reader,
                this
            );

            this._output = new SocketWriter
            (
                sendToSocket.Writer,
                this
            );
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

        internal void InputReaderCompleted(Exception? ex)
        {
            TrySetShutdown(ex, this, PipeShutdownKind.InputReaderCompleted);
            try
            {
                this.Socket.Shutdown(SocketShutdown.Receive);
            }
            catch
            { }
        }

        internal void OutputWriterCompleted(Exception? ex)
            => TrySetShutdown(ex, this, PipeShutdownKind.OutputWriterCompleted);

        protected override void DisposeCore(bool isDisposing)
        {
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
        }

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static TransportConnectionContext Create
        (
            Socket socket,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            PipeOptions? pipeOptions = null,
            IFeatureCollection? serverFeatureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
            => Create
            (
                socket,
                socketConnectionOptions,
                pipeOptions,
                pipeOptions,
                serverFeatureCollection,
                name,
                logger
            );

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static TransportConnectionContext Create
        (
            Socket socket,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            PipeOptions? sendPipeOptions = null,
            PipeOptions? receivePipeOptions = null,
            IFeatureCollection? serverFeatureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
            => Create
            (
                socket,
                new Pipe(sendPipeOptions ?? PipeOptions.Default),
                new Pipe(receivePipeOptions ?? PipeOptions.Default),
                socketConnectionOptions,
                sendPipeOptions?.ReaderScheduler ,
                receivePipeOptions?.WriterScheduler,
                serverFeatureCollection,
                name,
                logger
            );

        /// <summary>
        /// Create a SocketConnection instance over an existing socket
        /// </summary>
        internal static TransportConnectionContext Create
        (
            Socket socket,
            Pipe sendToSocket,
            Pipe receiveFromSocket,
            SocketConnectionOptions socketConnectionOptions = SocketConnectionOptions.None,
            PipeScheduler? sendScheduler = null,
            PipeScheduler? receiveScheduler = null,
            IFeatureCollection? serverFeatureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
            => new SocketConnectionContext
            (
                socket,
                sendToSocket,
                receiveFromSocket,
                sendScheduler ?? PipeScheduler.ThreadPool,
                receiveScheduler ?? PipeScheduler.ThreadPool,
                socketConnectionOptions,
                serverFeatureCollection,
                name,
                logger
            )
            .InitializeSendReceiveTasks();

        private static void SetDefaultSocketOptions(Socket socket)
        {
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
            Exception? ex,
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
    }
}