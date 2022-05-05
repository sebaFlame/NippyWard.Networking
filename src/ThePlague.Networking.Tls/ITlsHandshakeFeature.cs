using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

using OpenSSL.Core.SSL;

namespace ThePlague.Networking.Tls
{
    public interface ITlsHandshakeFeature
    {
        /// <summary>
        /// The protocol used for this connection
        /// </summary>
        SslProtocol Protocol { get; }

        /// <summary>
        /// The cipher used for this TLS pipe
        /// </summary>
        string Cipher { get; }

        /// <summary>
        /// Renegotiate the current TLS connection
        /// </summary>
        /// <returns>True when successfull or throws an <see cref="OpenSSL.Core.Error.OpenSslException"/></returns>
        ValueTask<bool> RenegotiateAsync(CancellationToken cancellationToken = default);
    }
}
