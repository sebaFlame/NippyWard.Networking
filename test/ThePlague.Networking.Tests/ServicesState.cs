using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

using ThePlague.Networking.Logging;

using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Tests
{
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
    }
}
