using System;
using System.IO;

namespace NippyWard.Networking.Transports.Proxy
{
    internal class ProxyException : IOException
    {
        public ProxyException(string message)
            : base(message)
        { }
    }
}
