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
    public class FileLogger : ILogger, IDisposable
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
