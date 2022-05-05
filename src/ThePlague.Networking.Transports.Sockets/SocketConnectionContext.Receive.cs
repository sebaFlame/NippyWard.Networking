using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public partial class SocketConnectionContext
    {
        private SocketAwaitableEventArgs _readerArgs;

        /// <summary>
        /// The total number of bytes read from the socket
        /// </summary>
        public long BytesRead => Interlocked.Read(ref this._totalBytesReceived);

        /// <summary>
        /// The number of bytes received in the last read
        /// </summary>
        public int LastReceived { get; private set; }

        private long _totalBytesReceived;

        long IMeasuredDuplexPipe.TotalBytesReceived => this.BytesRead;

        private async Task DoReceiveAsync()
        {
            Exception error = null;
            Socket socket = this.Socket;
            PipeWriter writer = this._receiveFromSocket.Writer;
            SocketAwaitableEventArgs readerArgs = null;
            bool zeroLengthReads = this.ZeroLengthReads;
            ValueTask<FlushResult> flushTask;
            FlushResult result;
            Memory<byte> buffer;

            this.DebugLog("starting receive loop");
            try
            {
                this._readerArgs = readerArgs = new SocketAwaitableEventArgs
                (
                    this.InlineReads ? null : this._receiveOptions.WriterScheduler
                );

                while(true)
                {
                    if(zeroLengthReads && socket.Available == 0)
                    {
                        this.DebugLog($"awaiting zero-length receive...");

                        DoReceive(socket, readerArgs, default);

                        await readerArgs;

                        this.DebugLog($"zero-length receive complete; now {Socket.Available} bytes available");

                        // this *could* be because data is now available, or it *could* be because of
                        // the EOF; we can't really trust Available, so now we need to do a non-empty
                        // read to find out which
                    }

                    buffer = writer.GetMemory(1);
                    this.DebugLog($"leased {buffer.Length} bytes from pipe");

                    try
                    {
                        this.DebugLog($"initiating socket receive...");

                        DoReceive(socket, readerArgs, buffer);

                        this.DebugLog(_readerArgs.IsCompleted ? "receive is sync" : "receive is async");

                        int bytesReceived = await readerArgs;

                        this.LastReceived = bytesReceived;

                        this.DebugLog($"received {bytesReceived} bytes ({_readerArgs.BytesTransferred}, {_readerArgs.SocketError})");

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

                    this.DebugLog("flushing pipe");

                    flushTask = writer.FlushAsync();

                    if(flushTask.IsCompletedSuccessfully)
                    {
                        result = flushTask.Result;
                        this.DebugLog("pipe flushed (sync)");
                    }
                    else
                    {
                        result = await flushTask;
                        this.DebugLog("pipe flushed (async)");
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

                this.DebugLog($"fail: {ex.SocketErrorCode}");

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

                this.DebugLog($"fail: {ex.SocketErrorCode}");

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

                this.DebugLog($"fail: {ex.SocketErrorCode}");

                error = ex;
            }
            catch(ObjectDisposedException)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadDisposed);

                this.DebugLog($"fail: disposed");

                if (!this._receiveAborted)
                {
                    error = new ConnectionAbortedException();
                }
            }
            catch(IOException ex)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadIOException);

                this.DebugLog($"fail - io: {ex.Message}");

                error = ex;
            }
            catch (Exception ex)
            {
                this.TrySetShutdown(PipeShutdownKind.ReadException);

                this.DebugLog($"fail: {ex.Message}");

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
                    this.DebugLog($"shutting down socket-receive");
                    this.Socket.Shutdown(SocketShutdown.Receive);
                }
                catch { }

                // close the *writer* half of the receive pipe; we won't
                // be writing any more, but callers can still drain the
                // pipe if they choose
                this.DebugLog($"marking {nameof(this.Input)} as complete");
                try
                {
                    writer.Complete(error);
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

            this.DebugLog(error == null ? "exiting with success" : $"exiting with failure: {error.Message}");
            //return error;
        }

#pragma warning disable RCS1231 // Make parameter ref read-only.
        private static void DoReceive(Socket socket, SocketAwaitableEventArgs args, Memory<byte> buffer)
#pragma warning restore RCS1231 // Make parameter ref read-only.
        {
            args.SetBuffer(buffer);

            if(!socket.ReceiveAsync(args))
            {
                args.Complete();
            }
        }
    }
}