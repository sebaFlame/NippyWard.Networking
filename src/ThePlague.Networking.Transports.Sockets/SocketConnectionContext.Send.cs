using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace ThePlague.Networking.Transports.Sockets
{
    public partial class SocketConnectionContext
    {
        /// <summary>
        /// The total number of bytes sent to the socket
        /// </summary>
        public long BytesSent => Interlocked.Read(ref this._totalBytesSent);

        long IMeasuredDuplexPipe.TotalBytesSent => this.BytesSent;

        private long _totalBytesSent;

        private SocketAwaitableEventArgs _writerArgs;

        private async Task DoSendAsync()
        {
            Exception error = null;
            Socket socket = this.Socket;
            SocketAwaitableEventArgs writerArgs = null;
            PipeReader reader = this._sendToSocket.Reader;
            PipeWriter writer = this._sendToSocket.Writer;
            ReadResult result;
            ValueTask<ReadResult> read;
            ReadOnlySequence<byte> buffer;

            try
            {
                this._writerArgs = writerArgs = new SocketAwaitableEventArgs
                (
                    this.InlineWrites ? null : this._sendOptions.ReaderScheduler
                );

                while(true)
                {
                    if(!reader.TryRead(out result))
                    {
                        read = reader.ReadAsync();

                        if(read.IsCompletedSuccessfully)
                        {
                            result = read.Result;
                        }
                        else
                        {
                            result = await read;
                        }
                    }

                    buffer = result.Buffer;

                    if(result.IsCanceled
                        || (result.IsCompleted && buffer.IsEmpty))
                    {
                        break;
                    }

                    try
                    {
                        if(!buffer.IsEmpty)
                        {
                            DoSend(socket, writerArgs, buffer);
                            Interlocked.Add
                            (
                                ref this._totalBytesSent,
                                await writerArgs
                            );
                        }
                        else if(result.IsCompleted)
                        {
                            break;
                        }
                    }
                    finally
                    {
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
                error = null;
            }
            catch(SocketException ex)
            {
                this.TrySetShutdown
                (
                    PipeShutdownKind.WriteSocketError, ex.SocketErrorCode
                );
                error = ex;
            }
            catch(ObjectDisposedException)
            {
                this.TrySetShutdown(PipeShutdownKind.WriteDisposed);
                error = null;
            }
            catch(IOException ex)
            {
                this.TrySetShutdown(PipeShutdownKind.WriteIOException);
                error = ex;
            }
            catch(Exception ex)
            {
                this.TrySetShutdown(PipeShutdownKind.WriteException);
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
                    socket.Shutdown(SocketShutdown.Send);
                }
                catch { }

                // close *both halves* of the send pipe; we're not
                // listening *and* we don't want anyone trying to write
                try
                {
                    writer.Complete(error);
                }
                catch
                { }

                try
                {
                    reader.Complete(error);
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

            //return error;
        }

        private static void DoSend(Socket socket, SocketAwaitableEventArgs args, in ReadOnlySequence<byte> buffer)
        {
            if(buffer.IsSingleSegment)
            {
                DoSend(socket, args, buffer.First);
                return;
            }

            //ensure buffer is null
            if(!args.MemoryBuffer.IsEmpty)
            {
                args.SetBuffer(null, 0, 0);
            }

            //initialize buffer list
            IList<ArraySegment<byte>> bufferList = GetBufferList(args, buffer);
            args.BufferList = bufferList;

            if(!socket.SendAsync(args))
            {
                args.Complete();
            }
        }

#pragma warning disable RCS1231 // Make parameter ref read-only.
        private static void DoSend(Socket socket, SocketAwaitableEventArgs args, ReadOnlyMemory<byte> memory)
#pragma warning restore RCS1231 // Make parameter ref read-only.
        {
            args.SetBuffer(MemoryMarshal.AsMemory(memory));

            if(!socket.SendAsync(args))
            {
                args.Complete();
            }
        }

        private static IList<ArraySegment<byte>> GetBufferList(SocketAsyncEventArgs args, in ReadOnlySequence<byte> buffer)
        {
            IList<ArraySegment<byte>> list = args?.BufferList;

            if(list is null)
            {
                list = new List<ArraySegment<byte>>();
            }
            else
            {
                // Buffers are pooled, so it's OK to root them until the next multi-buffer write.
                list.Clear();
            }

            ArraySegment<byte> segment;
            foreach(ReadOnlyMemory<byte> b in buffer)
            {
                if(!MemoryMarshal.TryGetArray(b, out segment))
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
    }
}