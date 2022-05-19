using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Tests
{
    public class LogWriter : IAsyncDisposable
    {
        public ChannelWriter<string> Writer => this._channelWriter;
        public static LogWriter Instance => _Instance;

        private ChannelWriter<string> _channelWriter;
        private Task _doLogWriter;

        private const string _FileName = "log";
        private static LogWriter _Instance;

        static LogWriter()
        {
            _Instance = new LogWriter();
        }

        private LogWriter()
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

            string path;

#if LOGBYDATE
            string filename = string.Concat(_FileName, "_", DateTime.Now.ToFileTime().ToString());

            if(!Directory.Exists("log"))
            {
                Directory.CreateDirectory("log");
            }

            path = Path.Combine("log", filename);
#else
            path = _FileName;
#endif
            
            FileStream file = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            //truncate file
            file.SetLength(0);
            TextWriter textWriter = new StreamWriter(file, Encoding.UTF8, -1, false);

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

        public async ValueTask DisposeAsync()
        {
            this._channelWriter.Complete();

            try
            {
                await this._doLogWriter;
            }
            catch
            { }
        }
    }

    public class ServicesState
    {
        public IServiceProvider ServiceProvider { get; private set; }

        public ServicesState()
        {
            this.ServiceProvider = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Trace);
                    //builder.AddDebug();
                    builder.AddProvider(new FileLoggerProvider(LogWriter.Instance));
                })
                .BuildServiceProvider();
        }

        private class FileLoggerProvider : ILoggerProvider
        {
            private ChannelWriter<string> _channelWriter;

            public FileLoggerProvider(LogWriter logWriter)
            {
                this._channelWriter = logWriter.Writer;
            }

            public ILogger CreateLogger(string categoryName)
            {
                return new FileLogger(this.Write);
            }

            public void Write(string line)
            {
                this._channelWriter.TryWrite(line);
            }

            public void Dispose()
            { }
        }

        private class FileLogger : ILogger, IDisposable
        {
            private readonly Action<string> _writeLine;

            public FileLogger(Action<string> writeLine)
            {
                this._writeLine = writeLine;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return this;
            }

            public bool IsEnabled(LogLevel logLevel)
                => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                this._writeLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {state}{(exception != null ? "\n" : string.Empty)}{exception}");
            }

            public void Dispose()
            {
                //NOP
            }
        }
    }
}
