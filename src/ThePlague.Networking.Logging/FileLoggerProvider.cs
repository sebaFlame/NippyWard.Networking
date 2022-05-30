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
        private readonly LogWriter _logWriter;

        public FileLoggerProvider(LogWriter logWriter)
        {
            this._logWriter = logWriter;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(this._logWriter);
        }

        public void Dispose()
        { }
    }
}
