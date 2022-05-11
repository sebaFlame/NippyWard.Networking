using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;

using System.IO.Pipelines;

using OpenSSL.Core.SSL.Buffer;
using OpenSSL.Core.SSL;
using OpenSSL.Core.X509;
using OpenSSL.Core.Keys;

namespace ThePlague.Networking.Tls
{
    internal partial class TlsPipe
    {
        #region Client Authentication wrapper methods
        public Task AuthenticateAsClientAsync
        (
            SslStrength? sslStrength = null,
            SslProtocol? sslProtocol = null,
            X509Store certificateStore = null,
            X509Certificate certificate = null,
            PrivateKey privateKey = null,
            ClientCertificateCallbackHandler clientCertificateCallbackHandler = null,
            RemoteCertificateValidationHandler remoteCertificateValidationHandler = null,
            SslSession previousSession = null,
            IEnumerable<string> ciphers = null,
            CancellationToken cancellationToken = default
        )
        {
            this._ssl = Ssl.CreateClientSsl
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

            return this.Authenticate
            (
                this._ssl,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as client using <paramref name="sslOptions"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            SslOptions sslOptions,
            CancellationToken cancellationToken = default
        )
        {
            this._ssl = Ssl.CreateClientSsl
            (
                sslOptions
            );

            return this.Authenticate
            (
                this._ssl,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as client using all defaults
        /// </summary>
        public Task AuthenticateAsClientAsync(CancellationToken cancellationToken = default)
            => this.AuthenticateAsClientAsync
            (
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using a strength preset
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            SslStrength sslStrength,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                sslStrength: sslStrength,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using protocol <paramref name="sslProtocol"/> using optional ciphers <paramref name="allowedCiphers"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            SslProtocol sslProtocol,
            IEnumerable<string> allowedCiphers = null,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                null,
                sslProtocol: sslProtocol,
                null,
                null,
                null,
                null,
                null,
                null,
                ciphers: allowedCiphers,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and validate remote certificate using <paramref name="remoteValidation"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            RemoteCertificateValidationHandler remoteValidation,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                null,
                null,
                null,
                null,
                null,
                null,
                remoteCertificateValidationHandler: remoteValidation,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and validate remote certificate using a certificate collection in <paramref name="caStore"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            X509Store caStore,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                null,
                null,
                certificateStore: caStore,
                null,
                null,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client using client ceritifacet <paramref name="clientCertificate"/> and client key <paramref name="clientKey"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            X509Certificate clientCertificate,
            PrivateKey clientKey,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                null,
                null,
                null,
                certificate: clientCertificate,
                privateKey: clientKey,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as client and pick a client certificate and client key using <paramref name="clientCertificateCallback"/>
        /// </summary>
        public Task AuthenticateAsClientAsync
        (
            ClientCertificateCallbackHandler clientCertificateCallback,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsClientAsync
            (
                null,
                null,
                null,
                null,
                null,
                clientCertificateCallbackHandler: clientCertificateCallback,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );
        #endregion

        #region server authentication wrappers
        public Task AuthenticateAsServerAsync
        (
            SslStrength? sslStrength = null,
            SslProtocol? sslProtocol = null,
            X509Store certificateStore = null,
            X509Certificate certificate = null,
            PrivateKey privateKey = null,
            ClientCertificateCallbackHandler clientCertificateCallbackHandler = null,
            RemoteCertificateValidationHandler remoteCertificateValidationHandler = null,
            SslSession previousSession = null,
            IEnumerable<string> ciphers = null,
            CancellationToken cancellationToken = default
        )
        {
            this._ssl = Ssl.CreateServerSsl
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

            return this.Authenticate
            (
                this._ssl,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using <paramref name="sslOptions"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            SslOptions sslOptions,
            CancellationToken cancellationToken = default
        )
        {
            this._ssl = Ssl.CreateServerSsl
            (
                sslOptions
            );

            return this.Authenticate
            (
                this._ssl,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using a shared context <paramref name="sslContext"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            SslContext sslContext,
            CancellationToken cancellationToken = default
        )
        {
            this._ssl = Ssl.CreateServerSsl
            (
                sslContext
            );

            return this.Authenticate
            (
                this._ssl,
                this._innerReader,
                this._decryptedReadBuffer,
                this._innerWriter,
                cancellationToken
            );
        }

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsServerAsync
            (
                null,
                null,
                null,
                certificate: serverCertificate,
                privateKey: serverKey,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// and verify client ceritificate using a certificate collection in <paramref name="caStore"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            X509Store caStore,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsServerAsync
            (
                null,
                null,
                certificateStore: caStore,
                certificate: serverCertificate,
                privateKey: serverKey,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// and verify client ceritificate using <paramref name="remoteCertificateValidationHandler"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            RemoteCertificateValidationHandler remoteCertificateValidationHandler,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsServerAsync
            (
                null,
                null,
                null,
                certificate: serverCertificate,
                privateKey: serverKey,
                null,
                remoteCertificateValidationHandler: remoteCertificateValidationHandler,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// using a strength preset
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            SslStrength sslStrength,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsServerAsync
            (
                sslStrength: sslStrength,
                null,
                null,
                certificate: serverCertificate,
                privateKey: serverKey,
                null,
                null,
                null,
                null,
                cancellationToken: cancellationToken
           );

        /// <summary>
        /// Authenticate as server using certificate <paramref name="serverCertificate"/> and key <paramref name="privateKey"/>
        /// using protocol <paramref name="sslProtocol"/> using optional ciphers <paramref name="allowedCiphers"/>
        /// </summary>
        public Task AuthenticateAsServerAsync
        (
            X509Certificate serverCertificate,
            PrivateKey serverKey,
            SslProtocol sslProtocol,
            IEnumerable<string> allowedCiphers = null,
            CancellationToken cancellationToken = default
        )
            => this.AuthenticateAsServerAsync
            (
                null,
                sslProtocol: sslProtocol,
                null,
                certificate: serverCertificate,
                privateKey: serverKey,
                null,
                null,
                null,
                ciphers: allowedCiphers,
                cancellationToken: cancellationToken
           );
        #endregion
    }
}
