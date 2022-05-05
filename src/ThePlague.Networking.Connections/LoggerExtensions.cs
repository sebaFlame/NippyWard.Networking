using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

#nullable enable

namespace ThePlague.Networking.Connections
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
    }
}
