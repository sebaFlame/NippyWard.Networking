using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Text;

namespace Benchmark.LegacySsl
{
    // code copied from https://github.com/davidfowl/BedrockFramework/
    public interface ITlsHandshakeFeature
    {
        SslProtocols Protocol { get; }

        CipherAlgorithmType CipherAlgorithm { get; }

        int CipherStrength { get; }

        HashAlgorithmType HashAlgorithm { get; }

        int HashStrength { get; }

        ExchangeAlgorithmType KeyExchangeAlgorithm { get; }

        int KeyExchangeStrength { get; }
    }
}
