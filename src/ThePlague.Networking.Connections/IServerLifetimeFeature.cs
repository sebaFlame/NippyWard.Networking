using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ThePlague.Networking.Connections
{
    public interface IServerLifetimeFeature
    {
        CancellationToken ServerShutdown { get; set; }

        void Shutdown();
    }
}
