using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using System.IO.Pipes;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Transports.Pipes
{
    internal partial class NamedPipeConnectionContext
    {
        /// <summary>
        /// The total number of bytes read
        /// </summary>
        public override long BytesRead
            => Interlocked.Read(ref this._totalBytesReceived);

        private long _totalBytesReceived;

        protected override async Task DoReceiveAsync()
        {
            await Task.Yield();

            Exception error = null;
            PipeStream inputStream = this._inputStream;
            PipeWriter writer = this._receiveFromEndpoint.Writer;
            CancellationToken cancellationToken = this.ConnectionClosed;
            ValueTask<FlushResult> flushTask;
            FlushResult result;
            Memory<byte> buffer;
            

            this.TraceLog("starting receive loop");
            try
            {
                while (true)
                {
                    buffer = writer.GetMemory(1);
                    this.TraceLog($"leased {buffer.Length} bytes from pipe");

                    try
                    {
                        this.TraceLog($"initiating named pipe receive...");

                        int bytesReceived;
                        ValueTask<int> readTask;

                        //pipestream contains a reusable IValueTaskSource
                        //only on Windows (!!!)
                        //unix implementation is using unix domain sockets
                        readTask = inputStream.ReadAsync(buffer, cancellationToken);

                        if (readTask.IsCompletedSuccessfully)
                        {
                            this.TraceLog("receive is sync");
                            bytesReceived = readTask.Result;
                        }
                        else
                        {
                            this.TraceLog("receive is async");
                            bytesReceived = await readTask;
                        }

                        this.TraceLog($"received {bytesReceived} bytes");

                        if (bytesReceived <= 0)
                        {
                            writer.Advance(0);
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

                    if (flushTask.IsCompletedSuccessfully)
                    {
                        result = flushTask.Result;
                        this.TraceLog("pipe flushed (sync)");
                    }
                    else
                    {
                        result = await flushTask;
                        this.TraceLog("pipe flushed (async)");
                    }

                    if (result.IsCompleted)
                    {
                        break;
                    }
                    if (result.IsCanceled)
                    {
                        break;
                    }
                }
            }
            catch (IOException ex)
            {
                this.TraceLog($"fail - io: {ex.Message}");

                error = ex;
            }
            catch(OperationCanceledException ex)
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
                this.TraceLog($"fail: {ex.Message}");

                error = new IOException(ex.Message, ex);
            }
            finally
            {
                try
                {
                    this.TraceLog($"shutting down named pipe receive");
                    inputStream.Close();
                }
                catch
                { }

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
            }

            this.TraceLog(error == null ? $"exiting with success ({this._totalBytesReceived} bytes received)" : $"exiting with failure ({this._totalBytesReceived} bytes received): {error.Message}");
            //return error;
        }
    }
}
