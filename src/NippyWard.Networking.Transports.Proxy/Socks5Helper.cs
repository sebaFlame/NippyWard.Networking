using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Buffers;
using System.Net.Sockets;
using System.Buffers.Binary;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;
using System.Diagnostics.CodeAnalysis;

namespace NippyWard.Networking.Transports.Proxy
{
    internal static class Socks5Helper
    {
        internal const ushort _SocksDefaultPort = 1080;

        private const byte SOCKS5_VERSION_NUMBER = 5;
        private const byte SOCKS5_AUTH_METHOD_NO_AUTHENTICATION_REQUIRED = 0;
        private const byte SOCKS5_AUTH_METHOD_USERNAME_PASSWORD = 2;
        private const byte SOCKS5_MAX_FIELD_LENGTH = 255;
        private const byte SOCKS5_SUBNEGOTIATION_VERSION = 1; // Socks5 username & password auth
        private const byte SOCKS5_CMD_REPLY_SUCCEEDED = 0;
        private const byte SOCKS5_CMD_REPLY_GENERAL_SOCKS_SERVER_FAILURE = 1;
        private const byte SOCKS5_CMD_REPLY_CONNECTION_NOT_ALLOWED_BY_RULESET = 2;
        private const byte SOCKS5_CMD_REPLY_NETWORK_UNREACHABLE = 3;
        private const byte SOCKS5_CMD_REPLY_HOST_UNREACHABLE = 4;
        private const byte SOCKS5_CMD_REPLY_CONNECTION_REFUSED = 5;
        private const byte SOCKS5_CMD_REPLY_TTL_EXPIRED = 6;
        private const byte SOCKS5_CMD_REPLY_COMMAND_NOT_SUPPORTED = 7;
        private const byte SOCKS5_CMD_REPLY_ADDRESS_TYPE_NOT_SUPPORTED = 8;
        private const byte SOCKS5_RESERVED = 0;
        private const byte SOCKS5_ADDRTYPE_IPV4 = 1;
        private const byte SOCKS5_ADDRTYPE_DOMAIN_NAME = 3; //do not use, always resolve
        private const byte SOCKS5_ADDRTYPE_IPV6 = 4;

        internal static async Task<IPEndPoint> ResolveProxyUri
        (
            Uri proxyUri
        )
        {
            IPEndPoint proxyEndpoint;
            string host = proxyUri.Host;
            int port = proxyUri.Port;

            if (IPAddress.TryParse(host, out IPAddress? ipAddress))
            {
                proxyEndpoint = new IPEndPoint(ipAddress, port);
            }
            else
            {
                IEnumerable<IPAddress> addresses;

                try
                {
                    addresses = (await Dns.GetHostAddressesAsync
                    (
                        host
                    )
                    .ConfigureAwait(false))
                    .Where(x => x.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (Exception ex)
                {
                    throw new ProxyException("No IPv4 address found");
                }

                proxyEndpoint = new IPEndPoint(addresses.First(), port);
            }

            return proxyEndpoint;
        }

        internal static async Task AuthenticateSocks5ProxyAsync
        (
            PipeWriter writer,
            PipeReader reader,
            NetworkCredential? proxyCredential = null,
            ILogger? logger = null
        )
        {
            // +----+----------+----------+
            // |VER | NMETHODS | METHODS  |
            // +----+----------+----------+
            // | 1  |    1     | 1 to 255 |
            // +----+----------+----------+

            //send hello
            Memory<byte> writeBuffer = writer.GetMemory(4);
            ReadOnlySequence<byte> readBuffer;
            ReadResult readResult;

            logger?.TraceLog(typeof(Socks5Helper).Name, "Initializing server hello");

            writeBuffer.Span[0] = SOCKS5_VERSION_NUMBER;
            writeBuffer.Span[2] = SOCKS5_AUTH_METHOD_NO_AUTHENTICATION_REQUIRED;

            if (proxyCredential is not null)
            {
                writeBuffer.Span[1] = 2;
                writeBuffer.Span[3] = SOCKS5_AUTH_METHOD_USERNAME_PASSWORD;
                writer.Advance(4);
            }
            else
            {
                writeBuffer.Span[1] = 1;
                writer.Advance(3);
            }

            logger?.TraceLog(typeof(Socks5Helper).Name, "Sending server hello");

            //flush welcome
            await writer.FlushAsync();

            //receive welcome reply

            // +----+--------+
            // |VER | METHOD |
            // +----+--------+
            // | 1  |   1    |
            // +----+--------+
            //
            //  If the selected METHOD is X'FF', none of the methods listed by the
            //  client are acceptable, and the client MUST close the connection.
            //
            //  The values currently defined for METHOD are:
            //   * X'00' NO AUTHENTICATION REQUIRED
            //   * X'01' GSSAPI
            //   * X'02' USERNAME/PASSWORD
            //   * X'03' to X'7F' IANA ASSIGNED
            //   * X'80' to X'FE' RESERVED FOR PRIVATE METHODS
            //   * X'FF' NO ACCEPTABLE METHODS

            //receive method from proxy server
            readResult = await reader.ReadAtLeastAsync(2);

            if (readResult.IsCompleted)
            {
                throw new ProxyException("Connection reset by peer");
            }

            readBuffer = readResult.Buffer;

            byte res;
            try
            {
                logger?.TraceLog(typeof(Socks5Helper).Name, "Received proxy METHOD reply");

                //veriy result version
                res = readBuffer.Slice(0, 1).FirstSpan[0];
                VerifyProtocolVersion(SOCKS5_VERSION_NUMBER, res);

                //get the method
                res = readBuffer.Slice(1, 1).FirstSpan[0];
            }
            finally
            {
                //advance past reply
                reader.AdvanceTo(readBuffer.GetPosition(2));
            }

            switch (res)
            {
                case SOCKS5_AUTH_METHOD_NO_AUTHENTICATION_REQUIRED:
                    break;
                case SOCKS5_AUTH_METHOD_USERNAME_PASSWORD:
                    await AuthenticateSocks5ProxyCoreAsync
                    (
                        writer,
                        reader,
                        proxyCredential,
                        logger
                    );
                    break;
                default:
                    throw new ProxyException("The proxy does not accept the supported proxy client authentication methods.");
            }
        }

        private static void VerifyProtocolVersion(byte expected, byte version)
        {
            if (expected != version)
            {
                throw new ProxyException("Unexpected socks version");
            }
        }

        private static async Task AuthenticateSocks5ProxyCoreAsync
        (
            PipeWriter writer,
            PipeReader reader,
            NetworkCredential? proxyCredential = null,
            ILogger? logger = null
        )
        {
            if(proxyCredential is null
                || object.ReferenceEquals(proxyCredential, CredentialCache.DefaultNetworkCredentials))
            {
                throw new ProxyException("The proxy destination requires a username and password for authentication.");
            }

            logger?.TraceLog(typeof(Socks5Helper).Name, "Authenticating");

            // +----+------+----------+------+----------+
            // |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
            // +----+------+----------+------+----------+
            // | 1  |  1   | 1 to 255 |  1   | 1 to 255 |
            // +----+------+----------+------+----------+

            Memory<byte> writeBuffer = writer.GetMemory(1);
            writeBuffer.Span[0] = SOCKS5_SUBNEGOTIATION_VERSION;
            writer.Advance(1);

            WriteEncodedStringWithLength(writer, proxyCredential.UserName);
            WriteEncodedStringWithLength(writer, proxyCredential.Password);

            logger?.TraceLog(typeof(Socks5Helper).Name, "Sending username/password");

            await writer.FlushAsync();

            //receive success or failure from proxy

            // +----+--------+
            // |VER | STATUS |
            // +----+--------+
            // | 1  |   1    |
            // +----+--------+
            ReadResult readResult;
            ReadOnlySequence<byte> readBuffer;

            readResult = await reader.ReadAtLeastAsync(2);

            if (readResult.IsCompleted)
            {
                throw new ProxyException("Connection reset by peer");
            }

            logger?.TraceLog(typeof(Socks5Helper).Name, "Received proxy STATUS reply");

            readBuffer = readResult.Buffer;

            try
            {
                //veriy result version
                byte res = readBuffer.Slice(0, 1).FirstSpan[0];
                VerifyProtocolVersion(SOCKS5_SUBNEGOTIATION_VERSION, res);

                //get success
                res = readBuffer.Slice(1, 1).FirstSpan[0];
                if (res != SOCKS5_CMD_REPLY_SUCCEEDED)
                {
                    throw new ProxyException("Proxy authentication failed");
                }
            }
            finally
            {
                //advance past reply
                reader.AdvanceTo(readBuffer.GetPosition(2));
            }

            logger?.TraceLog(typeof(Socks5Helper).Name, "Authentication completed");
        }

        private static byte WriteEncodedStringWithLength
        (
            PipeWriter writer,
            string value
        )
        {
            int count = Encoding.UTF8.GetByteCount(value);

            if(count > SOCKS5_MAX_FIELD_LENGTH)
            {
                throw new ProxyException("Field too long");
            }

            Span<byte> buffer = writer.GetSpan(count + 1);

            //set length
            buffer[0] = (byte)count;

            //encode text to UTF8
            Encoding.UTF8.GetBytes(value, buffer.Slice(1));

            //advance writer with length field and max 255 text length
            writer.Advance(++count);

            return (byte)count;
        }

        internal static async Task WriteSocks5ProxyCommand
        (
            ProxyCommand command,
            PipeWriter writer,
            IPEndPoint endpoint,
            ILogger? logger
        )
        {
            // +----+-----+-------+------+----------+----------+
            // |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
            // +----+-----+-------+------+----------+----------+
            // | 1  |  1  | X'00' |  1   | Variable |    2     |
            // +----+-----+-------+------+----------+----------+
            //
            // * VER protocol version: X'05'
            // * CMD
            //   * CONNECT X'01'
            //   * BIND X'02'
            //   * UDP ASSOCIATE X'03'
            // * RSV RESERVED
            // * ATYP address itemType of following address
            //   * IP V4 address: X'01'
            //   * DOMAINNAME: X'03'
            //   * IP V6 address: X'04'
            // * DST.ADDR desired destination address
            // * DST.PORT desired destination port in network octet order

            //get a span of the max length 4 + 16 + 2
            Memory<byte> buffer = writer.GetMemory(22);

            logger?.TraceLog(typeof(Socks5Helper).Name, $"Initializing proxy {command} command");

            buffer.Span[0] = SOCKS5_VERSION_NUMBER;
            buffer.Span[1] = (byte)command;
            buffer.Span[2] = SOCKS5_RESERVED;

            IPAddress address = endpoint.Address;
            int written;

            if(address.AddressFamily == AddressFamily.InterNetwork)
            {
                logger?.TraceLog(typeof(Socks5Helper).Name, "IPv4 address");

                buffer.Span[3] = SOCKS5_ADDRTYPE_IPV4;
                address.TryWriteBytes(buffer.Slice(4).Span, out written);
                
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                logger?.TraceLog(typeof(Socks5Helper).Name, "IPv4 address");

                buffer.Span[3] = SOCKS5_ADDRTYPE_IPV6;
                address.TryWriteBytes(buffer.Slice(4).Span, out written);
            }
            else
            {
                throw new ProxyException("Unkown address family");
            }

            //wrote 4 bytes before
            written += 4;

            BinaryPrimitives.WriteUInt16BigEndian
            (
                buffer.Slice(written).Span,
                (ushort)endpoint.Port
            );

            //2 bytes for the port
            written += 2;

            //advance the writer
            writer.Advance(written);

            logger?.TraceLog(typeof(Socks5Helper).Name, $"Wrote a {written} byte command");

            await writer.FlushAsync();
        }

        internal static async Task<IPEndPoint> VerifySocks5Command
        (
            PipeReader reader,
            ILogger? logger
        )
        {
            //  +----+-----+-------+------+----------+----------+
            //  |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
            //  +----+-----+-------+------+----------+----------+
            //  | 1  |  1  | X'00' |  1   | Variable |    2     |
            //  +----+-----+-------+------+----------+----------+
            // * VER protocol version: X'05'
            // * REP Reply field:
            //   * X'00' succeeded
            //   * X'01' general SOCKS server failure
            //   * X'02' connection not allowed by ruleset
            //   * X'03' Network unreachable
            //   * X'04' Host unreachable
            //   * X'05' Connection refused
            //   * X'06' TTL expired
            //   * X'07' Command not supported
            //   * X'08' Address itemType not supported
            //   * X'09' to X'FF' unassigned
            // * RSV RESERVED
            // * ATYP address itemType of following address
            //   * IP V4 address: X'01'
            //   * DOMAINNAME: X'03'
            //   * IP V6 address: X'04'
            // * BND.ADDR server bound address
            // * BND.PORT server bound port in network octet order

            ReadResult readResult;
            ReadOnlySequence<byte> readBuffer;
            int addressLength;

            logger?.TraceLog(typeof(Socks5Helper).Name, "Reading first 4 bytes of proxy command reply");

            //read atleast the first 4 bytes
            readResult = await reader.ReadAtLeastAsync(4);

            if (readResult.IsCompleted)
            {
                throw new ProxyException("Connection reset by peer");
            }

            readBuffer = readResult.Buffer;

            try
            {
                //veriy result version
                byte res = readBuffer.Slice(0, 1).FirstSpan[0];
                VerifyProtocolVersion(SOCKS5_VERSION_NUMBER, res);

                //get success
                res = readBuffer.Slice(1, 1).FirstSpan[0];
                if (res != SOCKS5_CMD_REPLY_SUCCEEDED)
                {
                    HandleProxyCommandError(res);
                }

                //get address type
                res = readBuffer.Slice(3, 1).FirstSpan[0];

                switch (res)
                {
                    case SOCKS5_ADDRTYPE_IPV4:
                        addressLength = 4;
                        break;
                    case SOCKS5_ADDRTYPE_IPV6:
                        addressLength = 16;
                        break;
                    default:
                        throw new ProxyException("Unkown address family");
                }
            }
            finally
            {
                //advance past reply
                reader.AdvanceTo(readBuffer.GetPosition(4));
            }

            logger?.TraceLog(typeof(Socks5Helper).Name, $"Reading next {addressLength + 2} bytes of proxy command reply");

            //read next bytes of addresLength + port length
            readResult = await reader.ReadAtLeastAsync(addressLength + 2);

            if (readResult.IsCompleted)
            {
                throw new ProxyException("Connection reset by peer");
            }

            readBuffer = readResult.Buffer;

            try
            {
                //copy the address
                byte[] addr = readBuffer.Slice(0, addressLength).ToArray();

                //parse the IP address
                IPAddress ipAddress = new IPAddress(addr);

                //parse the port
                byte[] prt = readBuffer.Slice(addressLength).ToArray();

                ushort port = BinaryPrimitives.ReadUInt16BigEndian
                (
                    prt
                );

                IPEndPoint endpoint = new IPEndPoint(ipAddress, port);

                logger?.TraceLog(typeof(Socks5Helper).Name, $"Received {endpoint} as BIND address");

                return endpoint;
            }
            finally
            {
                reader.AdvanceTo(readBuffer.GetPosition(addressLength + 2));
            }
        }

        [DoesNotReturn]
        private static void HandleProxyCommandError
        (
            byte response
        )
        {
            string proxyErrorText;

            switch (response)
            {
                case SOCKS5_CMD_REPLY_GENERAL_SOCKS_SERVER_FAILURE:
                    proxyErrorText = "a general socks destination failure occurred";
                    break;
                case SOCKS5_CMD_REPLY_CONNECTION_NOT_ALLOWED_BY_RULESET:
                    proxyErrorText = "the connection is not allowed by proxy destination rule set";
                    break;
                case SOCKS5_CMD_REPLY_NETWORK_UNREACHABLE:
                    proxyErrorText = "the network was unreachable";
                    break;
                case SOCKS5_CMD_REPLY_HOST_UNREACHABLE:
                    proxyErrorText = "the host was unreachable";
                    break;
                case SOCKS5_CMD_REPLY_CONNECTION_REFUSED:
                    proxyErrorText = "the connection was refused by the remote network";
                    break;
                case SOCKS5_CMD_REPLY_TTL_EXPIRED:
                    proxyErrorText = "the time to live (TTL) has expired";
                    break;
                case SOCKS5_CMD_REPLY_COMMAND_NOT_SUPPORTED:
                    proxyErrorText = "the command issued by the proxy client is not supported by the proxy destination";
                    break;
                case SOCKS5_CMD_REPLY_ADDRESS_TYPE_NOT_SUPPORTED:
                    proxyErrorText = "the address type specified is not supported";
                    break;
                default:
                    proxyErrorText = "Proxy authentication failed";
                    break;
            }

            throw new ProxyException(proxyErrorText);
        }
    }
}
