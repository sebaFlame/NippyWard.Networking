using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Logging
{
    public class FileLoggerProvider : ILoggerProvider
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
}
