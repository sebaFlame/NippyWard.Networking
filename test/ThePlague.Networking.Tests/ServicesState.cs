using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThePlague.Networking.Tests
{
    public class ServicesState
    {
        public IServiceProvider ServiceProvider { get; }

        public ServicesState()
        {
            this.ServiceProvider = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Debug);
                    builder.AddDebug();
                    //builder.AddProvider(new FileLoggerProvider());
                })
                .BuildServiceProvider();
        }

        private class FileLoggerProvider : ILoggerProvider
        {
            private const string _FileName = "log";
            private TextWriter _textWriter;
            private readonly object _lock;

            public FileLoggerProvider()
            {
                this._lock = new object();
                FileStream file = File.Open(_FileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                this._textWriter = new StreamWriter(file, Encoding.Unicode, -1, false);
            }

            public ILogger CreateLogger(string categoryName)
            {
                return new FileLogger(this.Write);
            }

            public void Write(string line)
            {
                lock(this._lock)
                {
                    this._textWriter.WriteLine(line);
                    this._textWriter.Flush();
                }
            }

            public void Dispose()
            {
                this._textWriter.Dispose();
            }
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
                this._writeLine(formatter(state, exception));
            }

            public void Dispose()
            {
                //NOP
            }
        }

        private class TestOutputLoggerProvider : ILoggerProvider
        {
            private ITestOutputHelper _testOutputHelper;

            public TestOutputLoggerProvider(ITestOutputHelper testOutputHelper)
            {
                this._testOutputHelper = testOutputHelper;
            }

            public ILogger CreateLogger(string categoryName)
                => new TestOutputLogger(this._testOutputHelper);

            public void Dispose()
            {
                //NOP
            }
        }

        private class TestOutputLogger : ILogger, IDisposable
        {
            private ITestOutputHelper _testOutputHelper;

            public TestOutputLogger(ITestOutputHelper testOutputHelper)
            {
                this._testOutputHelper = testOutputHelper;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return this;
            }

            public bool IsEnabled(LogLevel logLevel)
                => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                this._testOutputHelper.WriteLine(formatter(state, exception));
            }

            //to allow scopint
            public void Dispose()
            {
                //NOP
            }
        }
    }
}
