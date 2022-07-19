using System.Net.Sockets;

namespace NippyWard.Networking.Transports.Sockets
{
    public interface ISocketFeature
    {
        Socket Socket { get; }
    }
}
