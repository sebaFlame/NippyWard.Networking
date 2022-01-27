using System;

namespace ThePlague.Networking.Connections
{
    internal class EmptyServiceProvider : IServiceProvider
    {
        public static IServiceProvider Instance { get; } = new EmptyServiceProvider();

        public object GetService(Type serviceType) => null;
    }
}
