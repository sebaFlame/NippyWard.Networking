using System.Threading.Tasks;
using System.Buffers;

namespace ThePlague.Networking.Connections.Middleware
{
    public interface IMessageWriter<TMessage>
    {
        void WriteMessage(TMessage message, IBufferWriter<byte> output);
    }
}
