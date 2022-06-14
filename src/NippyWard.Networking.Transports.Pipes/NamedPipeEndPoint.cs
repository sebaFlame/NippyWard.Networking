using System.Net;
using System.Net.Sockets;

namespace NippyWard.Networking.Transports.Pipes
{
    public class NamedPipeEndPoint : EndPoint
    {
        public override AddressFamily AddressFamily
            => AddressFamily.Unspecified;

        public string ServerName { get; }
        public string PipeName { get; }

        public NamedPipeEndPoint
        (
            string pipeName,
            string serverName = "."
        )
        {
            this.ServerName = serverName;
            this.PipeName = pipeName;
        }

        public override string ToString()
            => $"Server = {this.ServerName}, Pipe = {this.PipeName}";
    }
}
