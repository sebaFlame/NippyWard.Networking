using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ThePlague.Networking.Connections;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Threading;
using System.Net.Sockets;

namespace ThePlague.Networking.Tests
{
    internal class TestConnectionContext : ConnectionContext
    {
        public EndPoint EndPoint { get; }

        public override string ConnectionId { get; set; }
        public override IDuplexPipe Transport { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override IDictionary<object, object?> Items { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override IFeatureCollection Features => this._features;
        public override CancellationToken ConnectionClosed { get => this._cts.Token; set => throw new NotSupportedException(); }

        private readonly IFeatureCollection _features;
        private readonly CancellationTokenSource _cts;

        public TestConnectionContext(EndPoint endpoint, string connectionId)
        {
            this.EndPoint = endpoint;
            this.ConnectionId = connectionId;
            this._features = new FeatureCollection();
            this._cts = new CancellationTokenSource();
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            this._cts.Cancel();
        }
    }

    internal class TestConnectionServer : IConnectionListener
    {
        public EndPoint EndPoint { get; }

        private Func<string> _createConnectionId;

        public TestConnectionServer(EndPoint endpoint, Func<string> createConnectionId)
        {
            this.EndPoint = endpoint;
            this._createConnectionId = createConnectionId;
        }

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
            => new ValueTask<ConnectionContext?>(new TestConnectionContext(new TestConnectionEndPoint(), this._createConnectionId()));

        public ValueTask DisposeAsync()
            => default;

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
            => default;
    }

    internal class TestConnectionEndPoint : EndPoint
    {
        public override AddressFamily AddressFamily => AddressFamily.Unknown;

        private Guid _id;

        public TestConnectionEndPoint()
        {
            this._id = Guid.NewGuid();
        }

        public override EndPoint Create(SocketAddress socketAddress)
            => new TestConnectionEndPoint();

        public override string ToString()
            => this._id.ToString();
    }

    internal class TestConnectionContextFactory : IConnectionFactory
    {
        private Func<string> _createConnectionId;

        public TestConnectionContextFactory(Func<string> createConnectionId)
        {
            this._createConnectionId = createConnectionId;
        }

        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            => new ValueTask<ConnectionContext>(new TestConnectionContext(endpoint, this._createConnectionId()));
    }

    internal class TestConnectionContextListenerFactory : IConnectionListenerFactory
    {
        private Func<string> _createConnectionId;

        public TestConnectionContextListenerFactory(Func<string> createConnectionId)
        {
            this._createConnectionId = createConnectionId;
        }

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            => new ValueTask<IConnectionListener>(new TestConnectionServer(endpoint, this._createConnectionId));
    }

    internal static class TestConnectionContextExtensions
    {
        public static ClientBuilder UseTestConnection(this ClientBuilder clientBuilder, Func<string> createConnectionId)
        {
            ILogger? logger = clientBuilder.ApplicationServices.CreateLogger<TestConnectionContextFactory>();

            IConnectionFactory connectionFactory = new TestConnectionContextFactory(createConnectionId);

            return clientBuilder
                .AddBinding<TestConnectionEndPoint>(connectionFactory);
        }

        public static ServerBuilder UseTestConnection(this ServerBuilder serverBuilder, EndPoint endPoint, Func<string> createConnectionId)
        {
            ILogger? logger = serverBuilder.ApplicationServices.CreateLogger<TestConnectionContextListenerFactory>();

            IConnectionListenerFactory connectionListenerFactory = new TestConnectionContextListenerFactory(createConnectionId);

            return serverBuilder
                .AddBinding(endPoint, connectionListenerFactory);
        }
    }
}
