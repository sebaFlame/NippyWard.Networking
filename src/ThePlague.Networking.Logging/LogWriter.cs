using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Logging
{
    public class LogWriter : IAsyncDisposable
    {
        public ChannelWriter<string> Writer => this._channelWriter;
        public static LogWriter Instance => _Instance;

        private ChannelWriter<string> _channelWriter;
        private Task _doLogWriter;

        private const string _LogFileName = "log";
        private static LogWriter _Instance;

        static LogWriter()
        {
#if LOGBYDATE
            _Instance = new LogWriter(Path.Combine("log", _LogFileName), true);
#else
            _Instance = new LogWriter(_LogFileName);
#endif
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
                foreach(string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if(string.IsNullOrWhiteSpace(currentPath))
                    {
                        currentPath = part;
                    }
                    else
                    {
                        currentPath = Path.Combine(currentPath, part);
                    }

                    if (!Directory.Exists(currentPath))
                    {
                        Directory.CreateDirectory(currentPath);
                    }
                }
            }

            FileStream file = File.Open(logFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

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
}
