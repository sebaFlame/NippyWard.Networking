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
        /// Configure the <see cref="Server"/> to only allow a single <see cref="ConnectionContext"/>
        /// per <see cref="EndPoint"/> binding.
        /// </summary>
        public bool AcceptSingleConnection { get; set; }

        /// <summary>
        /// Seconds to wait for connections to "gracefully" close
        /// </summary>
        public uint TimeOut { get; set; }

        public ServerContext()
        {
            this.Bindings = new Dictionary<EndPoint, IConnectionListenerFactory>();
        }
    }
}
