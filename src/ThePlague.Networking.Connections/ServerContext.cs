using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace ThePlague.Networking.Connections
{
    public class ServerContext
    {
        /// <summary>
        /// <see cref="IConnectionListenerFactory"/> binding per <see cref="EndPoint"/> instance
        /// </summary>
        public IDictionary<EndPoint, IConnectionListenerFactory> Bindings { get; }

        /// <summary>
        /// Configure the <see cref="Server"/> to only allow <see cref="MaxClients"/> clients.
        /// A value of 0 allow unlimited.
        /// </summary>
        public uint MaxClients { get; set; } = uint.MaxValue;

        /// <summary>
        /// Seconds to wait for connections to "gracefully" close
        /// </summary>
        public uint TimeOut { get; set; } = 10;

        public ServerContext()
        {
            this.Bindings = new Dictionary<EndPoint, IConnectionListenerFactory>();
        }
    }
}
