using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Connections;

using OpenSSL.Core.Keys;

namespace Benchmark
{
    public class SslTransferBenchmark : BaseSslBenchmark
    {
        [Params(1024, 4096, 16384)]
        public int BufferSize { get; set; }

        private OpenSSL.Core.X509.X509Certificate? _openSslCertificate;
        private PrivateKey? _openSslKey;
        private X509Certificate2? _certificate;
        private ConnectionContext? _socketServer, _socketClient;
        private ConnectionContext? _streamServer, _streamClient;
        private ConnectionDelegate? _streamClientDelegate, _streamServerDelegate;
        private ConnectionDelegate? _socketClientDelegate, _socketServerDelegate;
        private TaskCompletionSource? _streamServerConnectedTcs, _streamClientConnectedTcs, _streamServerTcs, _streamClientTcs;
        private TaskCompletionSource? _socketServerConnectedTcs, _socketClientConnectedTcs, _socketServerTcs, _socketClientTcs;

        [GlobalSetup(Target = nameof(StreamServerLegacySslTransfer))]
        public async Task StreamServerLegacySslTransferSetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._streamServer, this._streamClient) = await InitializeStream(_StreamListenerFactory, _StreamConnectionFactory, CreateIPEndPoint());
            this._streamServer.Items["done"] = (this._streamServerTcs = new TaskCompletionSource());
            this._streamServer.Items["connected"] = (this._streamServerConnectedTcs = new TaskCompletionSource());
            this._streamClient.Items["done"] = (this._streamClientTcs = new TaskCompletionSource());
            this._streamClient.Items["connected"] = (this._streamClientConnectedTcs = new TaskCompletionSource());

            this._streamServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._streamClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //do handhsake
            Task server = InitializeServerLegasySslDelegate(_ServiceProvider, _certificate, _LegacySslProtocl, true)(this._streamServer);
            Task client = InitializeClientLegasySslDelegate(_ServiceProvider, _LegacySslProtocl, true)(this._streamClient);

            await Task.WhenAll(this._streamClientConnectedTcs.Task, this._streamServerConnectedTcs.Task);
        }

        [Benchmark(Baseline = true)]
        public async Task StreamServerLegacySslTransfer()
        {
            Task server = this._streamServerDelegate!(this._streamServer!);
            Task client = this._streamClientDelegate!(this._streamClient!);

            //when both tasks have finished, ssl is waiting on done tcs
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(StreamServerLegacySslTransfer))]
        public async Task StreamServerLegacySslTransferCleanup()
        {
            this._streamServerTcs!.SetResult();
            this._streamClientTcs!.SetResult();

            await this._streamServer!.Transport.Output.CompleteAsync();
            await this._streamClient!.Transport.Input.CompleteAsync();

            await this._streamClient.Transport.Output.CompleteAsync();
            await this._streamServer.Transport.Input.CompleteAsync();

            await this._streamServer.DisposeAsync();
            await this._streamClient.DisposeAsync();
        }

        /* TODO: fix LegacySsl for pipes
        [GlobalSetup(Target = nameof(SocketServerLegacySslTransfer))]
        public async Task SocketServerLegacySslTransferSetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._streamServer, this._streamClient) = await InitializeSocket(_SocketListenerFactory, _SocketConnectionFactory, CreateIPEndPoint());
            this._streamServer.Items["done"] = (this._streamServerTcs = new TaskCompletionSource());
            this._streamServer.Items["connected"] = (this._streamServerConnectedTcs = new TaskCompletionSource());
            this._streamClient.Items["done"] = (this._streamClientTcs = new TaskCompletionSource());
            this._streamClient.Items["connected"] = (this._streamClientConnectedTcs = new TaskCompletionSource());

            this._streamServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._streamClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //do handhsake
            Task server = InitializeServerLegasySslDelegate(_ServiceProvider, _certificate, _LegacySslProtocl, true)(this._streamServer);
            Task client = InitializeClientLegasySslDelegate(_ServiceProvider, _LegacySslProtocl, true)(this._streamClient);

            await Task.WhenAll(this._streamClientConnectedTcs.Task, this._streamServerConnectedTcs.Task);
        }

        [Benchmark(Baseline = true)]
        public async Task SocketServerLegacySslTransfer()
        {
            Task server = this._streamServerDelegate!(this._streamServer!);
            Task client = this._streamClientDelegate!(this._streamClient!);

            //when both tasks have finished, ssl is waiting on done tcs
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(SocketServerLegacySslTransfer))]
        public async Task SocketServerLegacySslTransferCleanup()
        {
            this._streamServerTcs!.SetResult();
            this._streamClientTcs!.SetResult();

            await this._streamServer!.Transport.Output.CompleteAsync();
            await this._streamClient!.Transport.Input.CompleteAsync();

            await this._streamClient.Transport.Output.CompleteAsync();
            await this._streamServer.Transport.Input.CompleteAsync();

            await this._streamServer.DisposeAsync();
            await this._streamClient.DisposeAsync();
        }
        */

        [GlobalSetup(Target = nameof(SocketServerOpenSslTransfer))]
        public async Task SocketServerOpenSslTransferSetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._socketServer, this._socketClient) = await InitializeSocket(_SocketListenerFactory, _SocketConnectionFactory, CreateIPEndPoint());
            this._socketServer.Items["done"] = (this._socketServerTcs = new TaskCompletionSource());
            this._socketServer.Items["connected"] = (this._socketServerConnectedTcs = new TaskCompletionSource());
            this._socketClient.Items["done"] = (this._socketClientTcs = new TaskCompletionSource());
            this._socketClient.Items["connected"] = (this._socketClientConnectedTcs = new TaskCompletionSource());

            this._socketServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._socketClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //do handshake
            Task server = InitializeServerOpenSslDelegate(_ServiceProvider, this._openSslKey, this._openSslCertificate, _OpenSsslProtocol, true)(this._socketServer);
            Task client = InitializeClientOpenSslDelegate(_ServiceProvider, _OpenSsslProtocol, _Cipher, true)(this._socketClient);

            await Task.WhenAll(this._socketClientConnectedTcs.Task, this._socketServerConnectedTcs.Task);
        }

        [Benchmark]
        public async Task SocketServerOpenSslTransfer()
        {
            Task server = this._socketServerDelegate!(this._socketServer!);
            Task client = this._socketClientDelegate!(this._socketClient!);

            //when both tasks have finished, ssl is waiting on done tcs
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(SocketServerOpenSslTransfer))]
        public async Task SocketServerOpenSslTransferCleanup()
        {
            this._socketServerTcs!.SetResult();
            this._socketClientTcs!.SetResult();

            await this._socketServer!.Transport.Output.CompleteAsync();
            await this._socketClient!.Transport.Input.CompleteAsync();

            await this._socketClient.Transport.Output.CompleteAsync();
            await this._socketServer.Transport.Input.CompleteAsync();

            await this._socketServer.DisposeAsync();
            await this._socketClient.DisposeAsync();
        }

        [GlobalSetup(Target = nameof(StreamServerOpenSslTransfer))]
        public async Task StreamServerOpenSslTransferSetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._socketServer, this._socketClient) = await InitializeStream(_StreamListenerFactory, _StreamConnectionFactory, CreateIPEndPoint());
            this._socketServer.Items["done"] = (this._socketServerTcs = new TaskCompletionSource());
            this._socketServer.Items["connected"] = (this._socketServerConnectedTcs = new TaskCompletionSource());
            this._socketClient.Items["done"] = (this._socketClientTcs = new TaskCompletionSource());
            this._socketClient.Items["connected"] = (this._socketClientConnectedTcs = new TaskCompletionSource());

            this._socketServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._socketClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //do handshake
            Task server = InitializeServerOpenSslDelegate(_ServiceProvider, this._openSslKey, this._openSslCertificate, _OpenSsslProtocol, true)(this._socketServer);
            Task client = InitializeClientOpenSslDelegate(_ServiceProvider, _OpenSsslProtocol, _Cipher, true)(this._socketClient);

            await Task.WhenAll(this._socketClientConnectedTcs.Task, this._socketServerConnectedTcs.Task);
        }

        [Benchmark]
        public async Task StreamServerOpenSslTransfer()
        {
            Task server = this._socketServerDelegate!(this._socketServer!);
            Task client = this._socketClientDelegate!(this._socketClient!);

            //when both tasks have finished, ssl is waiting on done tcs
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(StreamServerOpenSslTransfer))]
        public async Task StreamServerOpenSslTransferCleanup()
        {
            this._socketServerTcs!.SetResult();
            this._socketClientTcs!.SetResult();

            await this._socketServer!.Transport.Output.CompleteAsync();
            await this._socketClient!.Transport.Input.CompleteAsync();

            await this._socketClient.Transport.Output.CompleteAsync();
            await this._socketServer.Transport.Input.CompleteAsync();

            await this._socketServer.DisposeAsync();
            await this._socketClient.DisposeAsync();
        }
    }
}
