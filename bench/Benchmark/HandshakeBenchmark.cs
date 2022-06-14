using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Connections;

using NippyWard.OpenSSL.Keys;

namespace Benchmark
{
    public class HandshakeBenchmark : BaseSslBenchmark
    {
        private NippyWard.OpenSSL.X509.X509Certificate? _openSslCertificate;
        private PrivateKey? _openSslKey;
        private X509Certificate2? _certificate;
        private ConnectionContext? _openSslServer, _legacySslServer, _openSslClient,_legacySslClient;
        private ConnectionDelegate? _legacyClientDelegate, _legacyServerDelegate;
        private ConnectionDelegate? _openSslClientDelegate, _openSslServerDelegate;

        [GlobalSetup(Target = nameof(LegacySslHandshake))]
        public async Task LegacySetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._legacySslServer, this._legacySslClient) = await InitializeStream(_StreamListenerFactory, _StreamConnectionFactory, CreateIPEndPoint());
            this._legacyClientDelegate = InitializeClientLegasySslDelegate(_ServiceProvider, _LegacySslProtocl);
            this._legacyServerDelegate = InitializeServerLegasySslDelegate(_ServiceProvider, _certificate, _LegacySslProtocl);
        }
        
        [Benchmark(Baseline = true)]
        public async Task LegacySslHandshake()
        {
            Task server = this._legacyServerDelegate!(this._legacySslServer!);
            Task client = this._legacyClientDelegate!(this._legacySslClient!);

            //when both tasks have finished, ssl has been disposed
            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(LegacySslHandshake))]
        public async Task LegacyCleanup()
        {
            await this._legacySslServer!.Transport.Output.CompleteAsync();
            await this._legacySslClient!.Transport.Input.CompleteAsync();

            await this._legacySslClient.Transport.Output.CompleteAsync();
            await this._legacySslServer.Transport.Input.CompleteAsync();

            await this._legacySslServer.DisposeAsync();
            await this._legacySslClient.DisposeAsync();

            this._openSslCertificate!.Dispose();
            this._openSslKey!.Dispose();
            this._certificate!.Dispose();
        }

        [GlobalSetup(Target = nameof(OpenSslHandshake))]
        public async Task OpenSslSetup()
        {
            InitializeCertificate(out this._openSslCertificate, out this._openSslKey, out this._certificate);

            (this._openSslServer, this._openSslClient) = await InitializeSocket(_SocketListenerFactory, _SocketConnectionFactory, CreateIPEndPoint());
            this._openSslServerDelegate = InitializeServerOpenSslDelegate(_ServiceProvider, this._openSslKey, this._openSslCertificate, _OpenSsslProtocol);
            this._openSslClientDelegate = InitializeClientOpenSslDelegate(_ServiceProvider, _OpenSsslProtocol, _Cipher);
        }

        [Benchmark]
        public async Task OpenSslHandshake()
        {
            //when both tasks have finished, tls has been disposed
            Task server = this._openSslServerDelegate!(this._openSslServer!);
            Task client = this._openSslClientDelegate!(this._openSslClient!);

            await Task.WhenAll(server, client);
        }

        [GlobalCleanup(Target = nameof(OpenSslHandshake))]
        public async Task OpenSslCleanup()
        {
            await this._openSslServer!.Transport.Output.CompleteAsync();
            await this._openSslClient!.Transport.Input.CompleteAsync();

            await this._openSslClient.Transport.Output.CompleteAsync();
            await this._openSslServer.Transport.Input.CompleteAsync();

            await this._openSslServer.DisposeAsync();
            await this._openSslClient.DisposeAsync();

            this._openSslCertificate!.Dispose();
            this._openSslKey!.Dispose();
            this._certificate!.Dispose();
        }
    }
}
