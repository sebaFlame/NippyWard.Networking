using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NippyWard.Networking.Logging
{
    public class FileLogger : ILogger, IDisposable
    {
        private readonly LogWriter _logWriter;
        private readonly string _categoryName;

        public FileLogger(string categoryName, LogWriter logWriter)
        {
            this._categoryName = categoryName;
            this._logWriter = logWriter;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return this;
        }

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            this._logWriter.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {this._categoryName} {state}{(exception != null ? "\n" : string.Empty)}{exception}");
        }

        public void Dispose()
        {
            //NOP
        }
    }
}
