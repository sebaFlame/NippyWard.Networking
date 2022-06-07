using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Sockets
{
    public partial class SocketConnectionContext
    {
        /// <summary>
        /// The total number of bytes sent
        /// </summary>
        public override long BytesSent
            => Interlocked.Read(ref this._totalBytesSent);

        private SocketAwaitableEventArgs? _writerArgs;
        private List<ArraySegment<byte>>? _spareBuffer;
        private long _totalBytesSent;

        protected override async Task DoSendAsync()
        {
            Exception? error = null;
            Socket socket = this.Socket;
            SocketAwaitableEventArgs writerArgs = new SocketAwaitableEventArgs
            (
                this._sendScheduler,
                this._logger
            );
            PipeReader reader = this._sendToEndpoint.Reader;
            PipeWriter writer = this._sendToEndpoint.Writer;
            CancellationToken cancellationToken = this.ConnectionClosed;
            ReadResult result;
            ValueTask<ReadResult> read;
            ReadOnlySequence<byte> buffer;

            this.TraceLog("starting send loop");
            try
            {
                this._writerArgs = writerArgs;

                while(true)
                {
                    this.TraceLog("awaiting data from pipe...");
                    if (!reader.TryRead(out result))
                    {
                        read = reader.ReadAsync();

                        if(read.IsCompletedSuccessfully)
                        {
                            result = read.Result;
                            this.TraceLog("sync async read");
                        }
                        else
                        {
                            result = await read;
                            this.TraceLog("async read");
                        }
                    }
                    else
                    {
                        this.TraceLog("sync read");
                    }

                    buffer = result.Buffer;

                    if(result.IsCanceled
                        || (result.IsCompleted && buffer.IsEmpty))
                    {
                        this.TraceLog(result.IsCanceled ? "cancelled" : "complete");
                        break;
                    }

                    try
                    {
                        ValueTask<int> socketTask;
                        int bytesSent;

                        if(!buffer.IsEmpty)
                        {
                            this.TraceLog($"sending {buffer.Length} bytes over socket...");

                            socketTask = DoSend
                            (
                                socket,
                                writerArgs,
                                buffer,
                                ref this._spareBuffer,
                                cancellationToken
                            );

                            this.TraceLog(socketTask.IsCompletedSuccessfully ? "send is sync" : "send is async");

                            if (socketTask.IsCompletedSuccessfully)
                            {
                                bytesSent = socketTask.Result;
                            }
                            else
                            {
                                bytesSent = await socketTask;
                            }

                            Interlocked.Add
                            (
                                ref this._totalBytesSent,
                                bytesSent
                            );
                        }
                        else if(result.IsCompleted)
                        {
                            this.TraceLog("completed");
                            break;
                        }
                    }
                    finally
                    {
                        this.TraceLog("advancing");
                        reader.AdvanceTo(buffer.End);
                    }
                }

                this.TrySetShutdown(PipeShutdownKind.WriteEndOfStream);
            }
            catch(SocketException ex)
                when
                (
                    ex.SocketErrorCode == SocketError.OperationAborted
                )
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.WriteSocketError, ex.SocketErrorCode
                );

                this.TraceLog($"fail: {ex.SocketErrorCode}");

                error = null;
            }
            catch(SocketException ex)
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.WriteSocketError, ex.SocketErrorCode
                );

                this.TraceLog($"fail: {ex.SocketErrorCode}");

                error = ex;
            }
            catch(ObjectDisposedException)
            {
                this.TrySetShutdown(PipeShutdownKind.WriteDisposed);

                this.TraceLog("fail: disposed");

                error = null;
            }
            catch(IOException ex)
            {
                this.TrySetShutdown(PipeShutdownKind.WriteIOException);

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
                this.TrySetShutdown(PipeShutdownKind.WriteException);

                this.TraceLog($"fail: {ex.Message}");

                error = new IOException(ex.Message, ex);
            }
            finally
            {
                // Make sure to close the connection only after the _aborted flag is set.
                // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
                // a BadHttpRequestException is thrown instead of a TaskCanceledException.
                this._sendAborted = true;
                try
                {
                    this.TraceLog($"shutting down socket-send");
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch { }

                // close *both halves* of the send pipe; we're not
                // listening *and* we don't want anyone trying to write
                this.TraceLog($"marking {nameof(this.Output)} as complete");
                try
                {
                    await writer.CompleteAsync(error);
                }
                catch
                { }

                try
                {
                    await reader.CompleteAsync(error);
                }
                catch
                { }

                TrySetShutdown
                (
                    error,
                    this,
                    PipeShutdownKind.OutputReaderCompleted
                );

                this._writerArgs = null;
                if(writerArgs is not null)
                {
                    try
                    {
                        writerArgs.Dispose();
                    }
                    catch
                    { }
                }
            }

            this.TraceLog(error == null ? $"exiting with success ({this._totalBytesSent} bytes sent)" : $"exiting with failure ({this._totalBytesSent} bytes sent): {error.Message}");
            //return error;
        }

        private static ValueTask<int> DoSend
        (
            Socket socket,
            SocketAwaitableEventArgs args,
            in ReadOnlySequence<byte> buffer,
            ref List<ArraySegment<byte>>? spareBuffer,
            CancellationToken cancellationToken = default
        )
        {
            if (buffer.IsSingleSegment)
            {
                return DoSend(socket, args, buffer.First, ref spareBuffer, cancellationToken);
            }

            //ensure buffer is null
            if (!args.MemoryBuffer.IsEmpty)
            {
                args.SetBuffer(null, 0, 0);
            }

            //initialize buffer list
            IList<ArraySegment<byte>> bufferList = GetBufferList(args, buffer, ref spareBuffer);
            args.BufferList = bufferList;

            return args.SendAsync(socket, cancellationToken);
        }

#pragma warning disable RCS1231 // Make parameter ref read-only.
        private static ValueTask<int> DoSend
        (
            Socket socket,
            SocketAwaitableEventArgs args,
            ReadOnlyMemory<byte> memory,
            ref List<ArraySegment<byte>>? spareBuffer,
            CancellationToken cancellationToken = default
        )
#pragma warning restore RCS1231 // Make parameter ref read-only.
        {
            RecycleSpareBuffer(args, ref spareBuffer);

            args.SetBuffer(MemoryMarshal.AsMemory(memory));

            return args.SendAsync(socket, cancellationToken);
        }

        private static IList<ArraySegment<byte>> GetBufferList(SocketAsyncEventArgs args, in ReadOnlySequence<byte> buffer, ref List<ArraySegment<byte>>? spareBuffer)
        {
            IList<ArraySegment<byte>>? list = args?.BufferList ?? GetSpareBuffer(ref spareBuffer);

            if (list is null)
            {
                list = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                list.Clear();
            }

            ArraySegment<byte> segment;
            foreach (ReadOnlyMemory<byte> b in buffer)
            {
                if (b.IsEmpty)
                {
                    continue;
                }

                if (!MemoryMarshal.TryGetArray(b, out segment))
                {
                    throw new InvalidOperationException
                    (
                        "MemoryMarshal.TryGetArray<byte> could not provide an array"
                    );
                }

                list.Add(segment);
            }

            return list;
        }

        private static List<ArraySegment<byte>>? GetSpareBuffer(ref List<ArraySegment<byte>>? spareBuffer)
        {
            List<ArraySegment<byte>>? existing = Interlocked.Exchange(ref spareBuffer, null);
            existing?.Clear();
            return existing;
        }

        private static void RecycleSpareBuffer(SocketAwaitableEventArgs args, ref List<ArraySegment<byte>>? spareBuffer)
        {
            // note: the BufferList getter is much less expensive then the setter.
            if (args?.BufferList is List<ArraySegment<byte>> list)
            {
                args.BufferList = null; // see #26 - don't want it being reused by the next piece of IO
                Interlocked.Exchange(ref spareBuffer, list);
            }
        }
    }
}