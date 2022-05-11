using System;

using Xunit;
using Xunit.Abstractions;

using ThePlague.Networking.Connections;

namespace ThePlague.Networking.Tests
{
    [Collection("logging")]
    public class SocketDataTests : BaseSocketDataTests
    {
        public SocketDataTests(ServicesState serviceState, ITestOutputHelper testOutputHelper)
            : base(serviceState, testOutputHelper)
        { }

        protected override ClientBuilder ConfigureClient(ClientBuilder clientBuilder)
            => clientBuilder;

        protected override ServerBuilder ConfigureServer(ServerBuilder serverBuilder)
            => serverBuilder;
    }
}
