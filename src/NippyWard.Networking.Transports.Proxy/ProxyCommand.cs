using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NippyWard.Networking.Transports.Proxy
{
    internal enum ProxyCommand : byte
    {
        CONNECT = 1,
        BIND = 2,
    }
}
