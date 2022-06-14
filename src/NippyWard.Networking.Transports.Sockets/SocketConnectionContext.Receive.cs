using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace NippyWard.Networking.Transports.Sockets
{
    public partial class SocketConnectionContext
    {
        /// <summary>
        /// The total number of bytes read
        /// </summary>
        public override long BytesRead
            => Interlocked.Read(ref this._totalBytesReceived);

        /// <summary>
        /// The number of bytes received in the last read
        /// </summary>
        public int LastReceived { get; private set; }

        private SocketAwaitableEventArgs? _readerArgs;
        private long _totalBytesReceived;

        protected override async Task DoReceiveAsync()
        {
            Exception? error = null;
            Socket socket = this.Socket;
            PipeWriter writer = this._receiveFromEndpoint.Writer;
            SocketAwaitableEventArgs readerArgs = new SocketAwaitableEventArgs
            (
                this._receiveScheduler,
                this._logger
            );
            bool zeroLengthReads = this.ZeroLengthReads;
            CancellationToken cancellationToken = this.ConnectionClosed;
            ValueTask<FlushResult> flushTask;
            FlushResult result;
            Memory<byte> buffer;

            this.TraceLog("starting receive loop");
            try
            {
                this._readerArgs = readerArgs;

                while(true)
                {
                    if(zeroLengthReads && socket.Available == 0)
                    {
                        this.TraceLog($"awaiting zero-length receive...");

                        ValueTask<int> socketTask = DoReceive(socket, readerArgs, default);

                        if(!socketTask.IsCompletedSuccessfully)
                        {
                            await socketTask;
                        }

                        this.TraceLog($"zero-length receive complete; now {socket.Available} bytes available");

                        // this *could* be because data is now available, or it *could* be because of
                        // the EOF; we can't really trust Available, so now we need to do a non-empty
                        // read to find out which
                    }

                    buffer = writer.GetMemory(1);
                    this.TraceLog($"leased {buffer.Length} bytes from pipe");

                    try
                    {
                        this.TraceLog($"initiating socket receive...");

                        int bytesReceived;
                        ValueTask<int> socketTask = DoReceive
                        (
                            socket,
                            readerArgs,
                            buffer,
                            cancellationToken
                        );

                        this.TraceLog(socketTask.IsCompletedSuccessfully ? "receive is sync" : "receive is async");

                        if(socketTask.IsCompletedSuccessfully)
                        {
                            bytesReceived = socketTask.Result;
                        }
                        else
                        {
                            bytesReceived = await socketTask;
                        }

                        this.LastReceived = bytesReceived;

                        this.TraceLog($"received {bytesReceived} bytes ({_readerArgs.BytesTransferred}, {_readerArgs.SocketError})");

                        if (bytesReceived <= 0)
                        {
                            writer.Advance(0);
                            this.TrySetShutdown
                            (
                                PipeShutdownKind.ReadEndOfStream
                            );
                            break;
                        }

                        writer.Advance(bytesReceived);
                        Interlocked.Add
                        (
                            ref this._totalBytesReceived,
                            bytesReceived
                        );
                    }
                    finally
                    {
                        // commit?
                    }

                    this.TraceLog("flushing pipe");

                    flushTask = writer.FlushAsync();

                    if(flushTask.IsCompletedSuccessfully)
                    {
                        result = flushTask.Result;
                        this.TraceLog("pipe flushed (sync)");
                    }
                    else
                    {
                        result = await flushTask;
                        this.TraceLog("pipe flushed (async)");
                    }

                    if(result.IsCompleted)
                    {
                        this.TrySetShutdown
                        (
                            PipeShutdownKind.ReadFlushCompleted
                        );
                        break;
                    }
                    if(result.IsCanceled)
                    {
                        this.TrySetShutdown
                        (
                            PipeShutdownKind.ReadFlushCanceled
                        );
                        break;
                    }
                }
            }
            catch(SocketException ex)
                when
                (
                    ex.SocketErrorCode == SocketError.ConnectionReset
                )
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.ReadSocketError, ex.SocketErrorCode
                );

                this.TraceLog($"fail: {ex.SocketErrorCode}");

                error = new ConnectionResetException(ex.Message, ex);
            }
            catch(SocketException ex)
                when
                (
                    ex.SocketErrorCode is SocketError.OperationAborted
                        or SocketError.ConnectionAborted
                        or SocketError.Interrupted
                        or SocketError.InvalidArgument
                )
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.ReadSocketError, ex.SocketErrorCode
                );

                this.TraceLog($"fail: {ex.SocketErrorCode}");

                if (!this._receiveAborted)
                {
                    // Calling Dispose after ReceiveAsync can cause an "InvalidArgument" error on *nix.
                    error = new ConnectionAbortedException();
                }
            }
            catch(SocketException ex)
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.ReadSocketError, ex.SocketErrorCode
                );

                this.TraceLog($"fail: {ex.SocketErrorCode}");

                error = ex;
            }
            catch(ObjectDisposedException)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadDisposed);

                this.TraceLog($"fail: disposed");

                if (!this._receiveAborted)
                {
                    error = new ConnectionAbortedException();
                }
            }
            catch(IOException ex)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadIOException);

                this.TraceLog($"fail - io: {ex.Message}");

                error = ex;
            }
            catch (OperationCanceledException ex)
            {
                if (ex.CancellationToken.Equals(this.ConnectionClosed))
                {
                    error = new ConnectionAbortedException();
                }
                else
                {
                    error = ex;
                }
            }
            catch (Exception ex)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadException);

                this.TraceLog($"fail: {ex.Message}");

                error = new IOException(ex.Message, ex);
            }
            finally
            {
                if(this._receiveAborted)
                {
                    error ??= new ConnectionAbortedException();
                }
                try
                {
                    this.TraceLog($"shutting down socket-receive");
                    socket.Shutdown(SocketShutdown.Receive);
                }
                catch { }

                // close the *writer* half of the receive pipe; we won't
                // be writing any more, but callers can still drain the
                // pipe if they choose
                this.TraceLog($"marking {nameof(this.Input)} as complete");
                try
                {
                    await writer.CompleteAsync(error);
                }
                catch
                { }

                TrySetShutdown(error, this, PipeShutdownKind.InputWriterCompleted);

                this._readerArgs = null;
                if(readerArgs is not null)
                {
                    try
                    {
                        readerArgs.Dispose();
                    }
                    catch
                    { }
                }
            }

            this.TraceLog(error == null ? $"exiting with success ({this._totalBytesReceived} bytes received)" : $"exiting with failure ({this._totalBytesReceived} bytes received): {error.Message}");

            if (error is not null)
            {
                throw error;
            }
        }

#pragma warning disable RCS1231 // Make parameter ref read-only.
        private static ValueTask<int> DoReceive
        (
            Socket socket,
            SocketAwaitableEventArgs args,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
#pragma warning restore RCS1231 // Make parameter ref read-only.
        {
            args.SetBuffer(buffer);

            return args.ReceiveAsync(socket, cancellationToken);
        }
    }
}