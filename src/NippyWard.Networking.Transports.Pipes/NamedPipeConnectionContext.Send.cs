﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Pipes;
using System.Buffers;
using System.IO;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace NippyWard.Networking.Transports.Pipes
{
    internal partial class NamedPipeConnectionContext
    {
        /// <summary>
        /// The total number of bytes sent
        /// </summary>
        public override long BytesSent
            => Interlocked.Read(ref this._totalBytesSent);

        private long _totalBytesSent;

        protected override async Task DoSendAsync()
        {
            Exception? error = null;
            PipeStream outputStream = this._outputStream;
            PipeReader reader = this._sendToEndpoint.Reader;
            PipeWriter writer = this._sendToEndpoint.Writer;
            CancellationToken cancellationToken = this.ConnectionClosed;
            ReadResult result;
            ValueTask<ReadResult> read;
            ReadOnlySequence<byte> buffer;

            this.TraceLog("starting send loop");
            try
            {
                while (true)
                {
                    this.TraceLog("awaiting data from pipe...");
                    if (!reader.TryRead(out result))
                    {
                        read = reader.ReadAsync();

                        if (read.IsCompletedSuccessfully)
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

                    if (result.IsCanceled
                        || (result.IsCompleted && buffer.IsEmpty))
                    {
                        this.TraceLog(result.IsCanceled ? "cancelled" : "complete");
                        break;
                    }

                    try
                    {
                        ValueTask<int> outputTask;
                        int bytesSent;

                        if (!buffer.IsEmpty)
                        {
                            this.TraceLog($"sending {buffer.Length} bytes over named pipe...");

                            outputTask = DoSendAsync
                            (
                                outputStream,
                                buffer,
                                cancellationToken
                            );

                            if (outputTask.IsCompletedSuccessfully)
                            {
                                this.TraceLog("send is sync");
                                bytesSent = outputTask.Result;
                            }
                            else
                            {
                                this.TraceLog("send is async");
                                bytesSent = await outputTask;
                            }

                            Interlocked.Add
                            (
                                ref this._totalBytesSent,
                                bytesSent
                            );
                        }
                        else if (result.IsCompleted)
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

#pragma warning disable CA1416 // Validate platform compatibility
                outputStream.WaitForPipeDrain();
#pragma warning restore CA1416 // Validate platform compatibility
            }
            catch (ObjectDisposedException)
            {
                this.TraceLog("fail: disposed");

                error = null;
            }
            catch (IOException ex)
            {
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
                this.TraceLog($"fail: {ex.Message}");

                error = new IOException(ex.Message, ex);
            }
            finally
            {
                try
                {
                    this.TraceLog($"shutting down named pipe send");
                    outputStream.Close();
                }
                catch
                { }

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
            }

            this.TraceLog(error == null ? $"exiting with success ({this._totalBytesSent} bytes sent)" : $"exiting with failure ({this._totalBytesSent} bytes sent): {error.Message}");

            if (error is not null)
            {
                throw error;
            }
        }

        //TODO: possible stack overflow
        private static async ValueTask<int> DoSendAsync
        (
            PipeStream pipeStream,
            ReadOnlySequence<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            ValueTask task;

            foreach(ReadOnlyMemory<byte> memory in buffer)
            {
                if(memory.IsEmpty)
                {
                    continue;
                }

                task = pipeStream.WriteAsync(memory, cancellationToken);

                //pipestream contains a reusable IValueTaskSource
                //only on Windows (!!!)
                //unix implementation is using unix domain sockets
                if (!task.IsCompletedSuccessfully)
                {
                    await task;
                }
            }

            await pipeStream.FlushAsync();

            return (int)buffer.Length;
        }
    }
}
