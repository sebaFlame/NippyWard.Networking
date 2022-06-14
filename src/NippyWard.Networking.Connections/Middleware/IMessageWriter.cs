using System.Threading.Tasks;
using System.Buffers;

namespace NippyWard.Networking.Connections.Middleware
{
    public interface IMessageWriter<TMessage>
    {
        /// <summary>
        /// Unparse a message and write the unparsed message to the
        /// <paramref name="output"/>.
        /// </summary>
        /// <param name="message">The message to unparse</param>
        /// <param name="output">The buffer to write to</param>
        void WriteMessage(TMessage message, IBufferWriter<byte> output);
    }
}
