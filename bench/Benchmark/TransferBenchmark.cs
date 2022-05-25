using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using BenchmarkDotNet.Attributes;

namespace Benchmark
{
    public class TransferBenchmark : BaseBenchmark
    {
        [Params(1024, 4096, 16384)]
        public int BufferSize { get; set; }

        private ConnectionContext? _socketServer, _socketClient;
        private ConnectionContext? _streamServer, _streamClient;
        private ConnectionDelegate? _streamClientDelegate, _streamServerDelegate;
        private ConnectionDelegate? _socketClientDelegate, _socketServerDelegate;

        private static byte[] _Buffer;

        static TransferBenchmark()
        {
            //1MB
            _Buffer = new byte[1024 * 1024];

            Span<byte> span = new Span<byte>(_Buffer);
            span.Fill(1);
        }

        [GlobalSetup(Target = nameof(LegacyServerTransfer))]
        public async Task LegacySetup()
        {
            (this._streamServer, this._streamClient) = await InitializeStream(_StreamListenerFactory, _StreamConnectionFactory, CreateIPEndPoint());
            this._streamServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._streamClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //warm-up (there's still some unwanted allocations during the benchmark)
            await this.LegacyServerTransfer();
        }

        [Benchmark(Baseline = true)]
        public async Task LegacyServerTransfer()
        {
            Task server = this._streamServerDelegate!(this._streamServer!);
            Task client = this._streamClientDelegate!(this._streamClient!);

            //when both tasks have finished, socket is still open
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(LegacyServerTransfer))]
        public async Task LegacyCleanup()
        {
            await this._streamServer!.Transport.Output.CompleteAsync();
            await this._streamClient!.Transport.Input.CompleteAsync();

            await this._streamClient.Transport.Output.CompleteAsync();
            await this._streamServer.Transport.Input.CompleteAsync();

            await this._streamServer.DisposeAsync();
            await this._streamClient.DisposeAsync();
        }

        [GlobalSetup(Target = nameof(SocketServerTransfer))]
        public async Task SocketSetup()
        {
            (this._socketServer, this._socketClient) = await InitializeSocket(_SocketListenerFactory, _SocketConnectionFactory, CreateIPEndPoint());
            this._socketServerDelegate = InitializeSendDelegate(_ServiceProvider, this.BufferSize, _Buffer);
            this._socketClientDelegate = InitializeReceiveDelegate(_ServiceProvider, _Buffer.Length);

            //warm-up (there's still some unwanted allocations during the benchmark)
            await this.SocketServerTransfer();
        }

        [Benchmark]
        public async Task SocketServerTransfer()
        {
            Task server = this._socketServerDelegate!(this._socketServer!);
            Task client = this._socketClientDelegate!(this._socketClient!);

            //when both tasks have finished, socket is still open
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(SocketServerTransfer))]
        public async Task SocketCleanup()
        {
            await this._socketServer!.Transport.Output.CompleteAsync();
            await this._socketClient!.Transport.Input.CompleteAsync();

            await this._socketClient.Transport.Output.CompleteAsync();
            await this._socketServer.Transport.Input.CompleteAsync();

            await this._socketServer.DisposeAsync();
            await this._socketClient.DisposeAsync();
        }
    }
}
