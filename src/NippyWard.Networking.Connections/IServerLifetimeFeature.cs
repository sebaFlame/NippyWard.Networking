using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace NippyWard.Networking.Connections
{
    public interface IServerLifetimeFeature
    {
        CancellationToken ServerShutdown { get; set; }

        void Shutdown(uint timeoutInSeconds);
    }
}
