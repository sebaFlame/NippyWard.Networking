using System;

namespace NippyWard.Networking.Tls
{
    public class TlsShutdownException : Exception
    {
        public TlsShutdownException()
            : base("TLS shutdown received from peer")
        { }
    }
}
