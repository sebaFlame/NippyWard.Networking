# NippyWard.OpenSSL
A ConnectionDelegate based .NET networking framework containing a high-performance TLS implementation.

## Rationale
I never liked the Stream API in .NET. The only alternative was using System.IO.Sockets directly. When System.IO.Pipelines was released, there finally was a clean alternative to streams. This project goes even further and uses the ASP.Net Core introduced ConnectionDelegate. This uses a ConnectionContext object which holds state and exposes the underlying connection through and IDuplexPipe.

This project can be summed up as a combination of
- [Bedrock Framework](https://github.com/davidfowl/BedrockFramework)
- [Pipelines.Sockets.Unofficial](https://github.com/mgravell/Pipelines.Sockets.Unofficial)
- My own [NippyWard.OpenSSL](https://github.com/sebaFlame/NippyWard.OpenSSL)
This project aims to merge and improve upon these projects. This project also containss a full TLS implemntation based on [NippyWard.OpenSSL](https://github.com/sebaFlame/NippyWard.OpenSSL) TLS API.

## Installation
Clone the repository with (!) submodules ([NippyWard.OpenSSL](deps/NippyWard.OpenSSL)).

## Usage
See [tests](test/NippyWard.Networking.Tests) for usage examples.

### Client
A client is built using fluent config and processing the connection using one or more middleware (delegates).
```C#
private const string _ClientHello = "Hello Server";

Task client = new ClientBuilder(serviceProvider)
    .UseSocket() //use sockets
    .ConfigureConnection //configure a middleware delegate, this can be called multiple times
    (
        (c) => //or reuse c (an IConnectionBuilder)
            c.Use
            (
                next =>
                (ConnectionContext ctx) =>
                {
                    //get the writer duplex half
                    PipeWriter writer = ctx.Transport.Output;

                    //rent a buffer
                    Memory<byte> buffer = writer.GetMemory(_ClientHello.Length * 2);

                    //copy the text to write to the buffer
                    MemoryMarshal.AsBytes<char>(_ClientHello).CopyTo(buffer.Span);

                    //advance the buffer by written bytes
                    writer.Advance(_ClientHello.Length * 2);

                    //write to the peer
                    await writer.FlushAsync();
                    
                    //close connection
                    await ctx.Transport.Output.CompleteAsync(); //always close output first
                    await ctx.Transport.Input.CompleteAsync();

                    //do not call the next middleware as the connection got closed
                    //this is a terminal
                    //await next(ctx);
                }
            )
    )
    .Build(endpoint);

await client;
```
This code constructs a Client using sockets as underlying transport.

When Build gets called, this client connects to the endpoint and sends "Hello Server" to the peer and disconnects using the configured middleware. Then the Client Task ends.

The built client gets exposed as a single (cancellable) task to the consumer. An IDisposable Client can be constructed by calling BuildClient. See [ClientBuilderTests](test/NippyWard.Networking.Tests/ClientBuilderTests.cs) for more usage examples.

### Server
A server is built using fluent config and processing every client connection using one or more middleware (delegates).
```C#
private const string _ServerHello = "Hello Client";

Task server = new ServerBuilder(serviceProvider)
    .UseSocket(endpoint) //use sockets
    .ConfigureMaxClients(1) //listen for a single client
    .ConfigureConnection //configure a middleware delegate, this can be called multiple times
    (
        (c) => //or reuse c (an IConnectionBuilder)
            c.Use
            (
                next =>
                (ConnectionContext ctx) =>
                {
                    //get the writer duplex half
                    PipeWriter writer = ctx.Transport.Output;

                    //rent a buffer
                    Memory<byte> buffer = writer.GetMemory(_ServerHello.Length * 2);

                    //copy the text to write to the buffer
                    MemoryMarshal.AsBytes<char>(_ServerHello).CopyTo(buffer.Span);

                    //advance the buffer by written bytes
                    writer.Advance(_ServerHello.Length * 2);

                    //write to the peer
                    await writer.FlushAsync();
                    
                    //close connection
                    await ctx.Transport.Output.CompleteAsync(); //always close output first
                    await ctx.Transport.Input.CompleteAsync();

                    //do not call the next middleware as the connection got closed
                    //this is a terminal
                    //await next(ctx);
                }
            )
    )
    .Build();

await server;
```
This code constructs a Server using sockets as underlying transport, listening for a single client.

When Build gets called, this server listens for a single client. When a client connects, the server sends "Hello Client" to the peer and disconnects using the configured middleware. After the single client got processed the Task ends.

The built server gets exposed as a single (cancellable) task to the consumer. An IDisposable Server can be constructed by calling BuildServer. See [ServerBuilderTests](test/NippyWard.Networking.Tests/ServerBuilderTests.cs) for more usage examples.

### TLS
```C#
//for client
new ClientBuilder(serviceProvider)
    .UseSocket() //use sockets
    .ConfigureConnection //use TLS, call this before setting up any other middleware
    (
        c => c.UseClientTls()
    )

//for server
new ServerBuilder(serviceProvider)
    .UseSocket(endpoint) //use sockets
    .ConfigureConnection //use TLS with serverCertificate and serverKey, call this before setting up any other middleware
    (
        c => c.UseServerTls(serverCertificate, serverKey)
    )
```