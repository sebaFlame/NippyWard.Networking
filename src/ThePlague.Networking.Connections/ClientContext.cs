using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace ThePlague.Networking.Connections
{
    public class ClientContext
    {
        /// <summary>
        /// <see cref="IConnectionFactory"/> binding per <see cref="EndPoint"/> type.
        /// </summary>
        public IDictionary<Type, IConnectionFactory> Bindings { get; }

        public ClientContext()
        {
            this.Bindings = new Dictionary<Type, IConnectionFactory>();
        }
    }
}
