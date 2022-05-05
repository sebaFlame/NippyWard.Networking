using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ThePlague.Networking.Tls
{
    internal enum TlsPipeState
    {
        SYNCHRONIZED = 0,
        WANTREAD = 1 << 0,
        WANTWRITE = 1 << 1
    }
}
