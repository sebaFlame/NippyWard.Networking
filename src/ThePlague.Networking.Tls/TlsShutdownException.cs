using System;

namespace ThePlague.Networking.Tls
{
    public class TlsShutdownException : Exception
    {
        public TlsShutdownException()
            : base("TLS shutdown received from peer")
        { }
    }
}
