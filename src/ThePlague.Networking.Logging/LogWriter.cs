using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Logging
{
    public class LogWriter : TextWriter, IAsyncDisposable
    {
        public override Encoding Encoding => Encoding.UTF8;

        public ChannelWriter<string> Writer => this._channelWriter;
        public static LogWriter Instance
        {
            get
            {
                lock(_InstanceLock)
                {
                    if (_Instance is null)
                    {
#if LOGBYDATE
                        _Instance = new LogWriter(Path.Combine(_LogDirName, _LogFileName), true);
#else
                        _Instance = new LogWriter(_LogFileName);
#endif

#if TRACELISTENER
                        TextWriterTraceListener trace = new TextWriterTraceListener(_Instance, _LogFileName);
                        Trace.Listeners.Add(trace);
#endif
                    }
                }

                return _Instance;
            }
        }

        private ChannelWriter<string> _channelWriter;
        private Task _doLogWriter;

        private const string _LogFileName = "log";
        private const string _LogDirName = "logs";

        private static LogWriter? _Instance;
        private static readonly object _InstanceLock;

        static LogWriter()
        {
            _InstanceLock = new object();
        }

        public LogWriter(string logFilePath, bool logByDate = false)
        {
            Channel<string> channel = Channel.CreateUnbounded<string>
            (
                new UnboundedChannelOptions()
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                }
            );

            string path = logFilePath;

            if (logByDate)
            {
                string filePath = string.Concat(logFilePath, "-", DateTime.Now.ToFileTime().ToString());
                path = Path.GetFullPath(filePath);

                string currentPath = string.Empty;
                string[] parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    currentPath = Path.Combine(currentPath, parts[i]);

                    if (!Directory.Exists(currentPath))
                    {
                        Directory.CreateDirectory(currentPath);
                    }
                }
            }

            FileStream file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, false);

            //truncate file
            file.SetLength(0);
            TextWriter textWriter = new StreamWriter(file, this.Encoding, -1, false);

            this._channelWriter = channel.Writer;
            this._doLogWriter = DoLogWrite(textWriter, channel.Reader);
        }

        private static async Task DoLogWrite
        (
            TextWriter textWriter,
            ChannelReader<string> channelReader
        )
        {
            await Task.Yield();

            ValueTask<string> lineTask;
            string line;

            try
            {
                while (true)
                {
                    lineTask = channelReader.ReadAsync();

                    if (lineTask.IsCompletedSuccessfully)
                    {
                        line = lineTask.Result;
                    }
                    else
                    {
                        line = await lineTask;
                    }

                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    do
                    {
                        if (string.IsNullOrEmpty(line))
                        {
                            continue;
                        }

                        await textWriter.WriteLineAsync(line);
                    } while (channelReader.TryRead(out line));

                    await textWriter.FlushAsync();
                }
            }
            finally
            {
                await textWriter.DisposeAsync();
            }
        }

        public override void Write(string? value)
        {
            if(value is null)
            {
                return;
            }

            this._channelWriter.TryWrite(value);
        }

        public override Task WriteAsync(string? value)
        {
            if (value is null)
            {
                return Task.CompletedTask;
            }

            return this._channelWriter.WriteAsync(value).AsTask();
        }

        public override void WriteLine(string? value)
        {
            if(value is null)
            {
                return;
            }

            this._channelWriter.TryWrite(value);
        }

        public override Task WriteLineAsync(string? value)
        {
            if (value is null)
            {
                return Task.CompletedTask;
            }

            return this._channelWriter.WriteAsync(value).AsTask();
        }

        public override async ValueTask DisposeAsync()
        {
            this._channelWriter.Complete();

            try
            {
                await this._doLogWriter;
            }
            catch
            { }

            await base.DisposeAsync();
        }

        public override void Close()
        {
            //NOP
        }

        protected override void Dispose(bool disposing)
        {
            //NOP
        }
    }
}
