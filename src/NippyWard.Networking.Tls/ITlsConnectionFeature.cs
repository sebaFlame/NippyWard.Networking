using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using NippyWard.OpenSSL.X509;
using NippyWard.OpenSSL.SSL;

namespace NippyWard.Networking.Tls
{
    public interface ITlsConnectionFeature
    {
        /// <summary>
        /// The certificate used to establish the TLS pipe.
        /// </summary>
        X509Certificate? Certificate { get; }

        /// <summary>
        /// The certificate received from remote. 
        /// This is the client certificate (if any) when called from server.
        /// </summary>
        X509Certificate? RemoteCertificate { get; }

        /// <summary>
        /// The (reusable) session for this (client) connection.
        /// </summary>
        SslSession? Session { get; }
    }
}
