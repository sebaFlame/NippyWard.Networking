using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

using NippyWard.OpenSSL.SSL;

namespace NippyWard.Networking.Tls
{
    public interface ITlsHandshakeFeature
    {
        /// <summary>
        /// The protocol used for this connection
        /// </summary>
        SslProtocol? Protocol { get; }

        /// <summary>
        /// The cipher used for this TLS pipe
        /// </summary>
        string? Cipher { get; }

        /// <summary>
        /// Initialize a renegotiation while you're reading
        /// </summary>
        Task InitializeRenegotiateAsync();

        /// <summary>
        /// Renegotiate the current TLS connection
        /// </summary>
        Task RenegotiateAsync();

        /// <summary>
        /// Initialize a shutdown while you're reading
        /// </summary>
        Task InitializeShutdownAsync();

        /// <summary>
        /// Shutdown the current TLS connection
        /// </summary>
        Task ShutdownAsync();
    }
}
