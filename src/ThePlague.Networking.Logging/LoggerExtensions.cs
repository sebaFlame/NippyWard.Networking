using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Logging
{
    public static class LoggerExtensions
    {
        public static ILogger? CreateLogger<T>(this IServiceProvider serviceProvider)
        {
            ILoggerFactory? loggerFactory = (ILoggerFactory?)serviceProvider.GetService(typeof(ILoggerFactory));

            if (loggerFactory is null)
            {
                return (ILogger?)serviceProvider.GetService(typeof(ILogger));
            }
            else
            {
                return loggerFactory?.CreateLogger<T>();
                
            }
        }

        public static ILoggingBuilder AddFileLogger(this ILoggingBuilder loggingBuilder, string logFilePath, bool logByDate = false)
            => loggingBuilder.AddProvider(new FileLoggerProvider(new LogWriter(logFilePath, logByDate)));

        [Conditional("TRACELOG")]
        public static void TraceLog
        (
            this ILogger logger,
            string identifier,
            string message,
            [CallerFilePath] string? file = null,
            [CallerMemberName] string? caller = null,
            [CallerLineNumber] int lineNumber = 0
        )
            => TraceLog(logger, identifier, message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");

        public static void TraceLog
        (
            this ILogger logger,
            string identifier,
            string message,
            string caller
        )
        {
#if TRACELOG
            logger.LogTrace($"[{Thread.CurrentThread.ManagedThreadId.ToString()}, {identifier}, {caller}] {message}");
#endif
        }
    }
}
