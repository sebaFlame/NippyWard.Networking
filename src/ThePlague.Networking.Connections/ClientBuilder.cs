using System;

using Microsoft.AspNetCore.Connections;

namespace ThePlague.Networking.Connections
{
    public class ClientBuilder : IConnectionBuilder
    {
        public IServiceProvider ApplicationServices
            => this._connectionBuilder.ApplicationServices;

        private readonly ConnectionBuilder _connectionBuilder;
        private bool _useProtocol;

        public ClientBuilder()
            : this(EmptyServiceProvider.Instance)
        { }

        public ClientBuilder(IServiceProvider serviceProvider)
        {
            this._connectionBuilder = new ConnectionBuilder(serviceProvider);
        }

        private ClientBuilder UseClientBuilder
        (
            Func<ConnectionDelegate, ConnectionDelegate> middleware
        )
        {
            this._connectionBuilder.Use(middleware);
            return this;
        }

        public IConnectionBuilder Use
        (
            Func<ConnectionDelegate, ConnectionDelegate> middleware
        )
            => this.UseClientBuilder(middleware);

        public ConnectionDelegate Build()
        {
            //ensure the delegate blocks on the most outer delegate
            if(!this._useProtocol)
            {
                this.UseClientBuilder
                (
                    next => new ConnectionTerminal().OnConnectionAsync
                );
            }

            return this._connectionBuilder.Build();
        }

        /// <summary>
        /// Use a message parser and dispatcher
        /// </summary>
        public ClientBuilder UseProtocol<TMessage>
        (
            IMessageReader<TMessage> messageReader,
            IMessageWriter<TMessage> messageWriter,
            IMessageDispatcher<TMessage> messageDispatcher
        )
        {
            if(this._useProtocol)
            {
                throw new InvalidOperationException
                (
                    "A terminal has already been defined"
                );
            }

            this._useProtocol = true;
            return this.UseClientBuilder
            (
                next => new Protocol<TMessage>
                (
                    messageReader,
                    messageWriter,
                    messageDispatcher
                ).OnConnectionAsync
            );
        }
    }
}