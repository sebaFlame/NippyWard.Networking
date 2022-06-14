using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

using NippyWard.OpenSSL.SSL.Buffer;
using NippyWard.OpenSSL.SSL;
using NippyWard.OpenSSL.X509;
using NippyWard.OpenSSL.Keys;

namespace NippyWard.Networking.Tls
{
    internal partial class TlsPipe
    {
        private static async Task<TlsPipe> Authenticate
        (
            Ssl ssl,
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            TlsBuffer decryptedReadBuffer = new TlsBuffer(pool);
            TlsBuffer unencryptedWriteBuffer = new TlsBuffer(pool);

            await Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                decryptedReadBuffer,
                innerWriter,
                logger,
                cancellationToken
            );

            return new TlsPipe
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                decryptedReadBuffer,
                unencryptedWriteBuffer,
                logger
            );
        }

        #region Client Authentication wrapper methods
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            MemoryPool<byte>? pool = null,
            SslStrength? sslStrength = null,
            SslProtocol? sslProtocol = null,
            X509Store? certificateStore = null,
            X509Certificate? certificate = null,
            PrivateKey? privateKey = null,
            ClientCertificateCallbackHandler? clientCertificateCallbackHandler = null,
            RemoteCertificateValidationHandler? remoteCertificateValidationHandler = null,
            SslSession? previousSession = null,
            IEnumerable<string>? ciphers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            Ssl ssl = Ssl.CreateClientSsl
            (
                sslStrength: sslStrength,
                sslProtocol: sslProtocol,
                certificateStore: certificateStore,
                certificate: certificate,
                privateKey: privateKey,
                clientCertificateCallbackHandler: clientCertificateCallbackHandler,
                remoteCertificateValidationHandler: remoteCertificateValidationHandler,
                previousSession: previousSession,
                ciphers: ciphers
            );

            return Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                pool,
                logger,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as client using <paramref name="sslOptions"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            SslOptions sslOptions,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            Ssl ssl = Ssl.CreateClientSsl
            (
                sslOptions
            );

            return Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                pool,
                logger,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as client using all defaults
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using a strength preset
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            SslStrength sslStrength,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId,
                innerReader,
                innerWriter,
                sslStrength: sslStrength,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using protocol <paramref name="sslProtocol"/> using optional ciphers <paramref name="allowedCiphers"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            SslProtocol sslProtocol,
            MemoryPool<byte>? pool = null,
            IEnumerable<string>? allowedCiphers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                sslProtocol: sslProtocol,
                ciphers: allowedCiphers,                                
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and validate remote certificate using <paramref name="remoteValidation"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            RemoteCertificateValidationHandler remoteValidation,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                remoteCertificateValidationHandler: remoteValidation,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and validate remote certificate using a certificate collection in <paramref name="caStore"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Store caStore,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                certificateStore: caStore,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using client ceritifacet <paramref name="clientCertificate"/> and client key <paramref name="clientKey"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate clientCertificate,
            PrivateKey clientKey,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                certificate: clientCertificate,
                privateKey: clientKey,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and pick a client certificate and client key using <paramref name="clientCertificateCallback"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsClientAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            ClientCertificateCallbackHandler clientCertificateCallback,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsClientAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                clientCertificateCallbackHandler: clientCertificateCallback,
                logger: logger,
                cancellationToken: cancellationToken
           );
        #endregion

        #region server authentication wrappers
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            MemoryPool<byte>? pool = null,
            SslStrength? sslStrength = null,
            SslProtocol? sslProtocol = null,
            X509Store? certificateStore = null,
            X509Certificate? certificate = null,
            PrivateKey? privateKey = null,
            ClientCertificateCallbackHandler? clientCertificateCallbackHandler = null,
            RemoteCertificateValidationHandler? remoteCertificateValidationHandler = null,
            SslSession? previousSession = null,
            IEnumerable<string>? ciphers = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            Ssl ssl = Ssl.CreateServerSsl
            (
                sslStrength: sslStrength,
                sslProtocol: sslProtocol,
                certificateStore: certificateStore,
                certificate: certificate,
                privateKey: privateKey,
                clientCertificateCallbackHandler: clientCertificateCallbackHandler,
                remoteCertificateValidationHandler: remoteCertificateValidationHandler,
                previousSession: previousSession,
                ciphers: ciphers
            );

            return Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                pool,
                logger,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using <paramref name="sslOptions"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            SslOptions sslOptions,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            Ssl ssl = Ssl.CreateServerSsl
            (
                sslOptions
            );

            return Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                pool,
                logger,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using a shared context <paramref name="sslContext"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            SslContext sslContext,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
        {
            Ssl ssl =  Ssl.CreateServerSsl
            (
                sslContext
            );

            return Authenticate
            (
                ssl,
                connectionId,
                innerReader,
                innerWriter,
                pool,
                logger,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsServerAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                pool: pool,
                certificate: serverCertificate,
                privateKey: serverKey,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// and verify client ceritificate using a certificate collection in <paramref name="caStore"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            X509Store caStore,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsServerAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                certificateStore: caStore,
                certificate: serverCertificate,
                privateKey: serverKey,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// and verify client ceritificate using <paramref name="remoteCertificateValidationHandler"/>
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            RemoteCertificateValidationHandler remoteCertificateValidationHandler,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsServerAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                certificate: serverCertificate,
                privateKey: serverKey,
                remoteCertificateValidationHandler: remoteCertificateValidationHandler,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// using a strength preset
        /// </summary>
        public static Task<TlsPipe> AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            SslStrength sslStrength,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsServerAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter,
                sslStrength: sslStrength,
                certificate: serverCertificate,
                privateKey: serverKey,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// using protocol <paramref name="sslProtocol"/> using optional ciphers <paramref name="allowedCiphers"/>
        /// </summary>
        public static Task<TlsPipe>  AuthenticateAsServerAsync
        (
            string connectionId,
            PipeReader innerReader,
            PipeWriter innerWriter,
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            SslProtocol sslProtocol,
            IEnumerable<string>? allowedCiphers = null,
            MemoryPool<byte>? pool = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default
        )
            => AuthenticateAsServerAsync
            (
                connectionId: connectionId,
                innerReader: innerReader,
                innerWriter: innerWriter, sslProtocol: sslProtocol,
                certificate: serverCertificate,
                privateKey: serverKey,
                ciphers: allowedCiphers,
                pool: pool,
                logger: logger,
                cancellationToken: cancellationToken
           );
        #endregion
    }
}
