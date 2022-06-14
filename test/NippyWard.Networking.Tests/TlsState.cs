using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using NippyWard.OpenSSL.X509;
using NippyWard.OpenSSL.Keys;
using NippyWard.OpenSSL.ASN1;

namespace NippyWard.Networking.Tests
{
    public class TlsState : IDisposable
    {
        public X509Certificate CACertificate => this._ca.Certificate;
        public PrivateKey CAKey => this._ca.Key;

        public X509Certificate ServerCertificate => this._serverCertificate;
        public PrivateKey ServerKey => this._serverKey;

        public X509Certificate ClientCertificate => this._clientCertificate;
        public PrivateKey ClientKey => this._clientKey;

        private X509CertificateAuthority _ca;
        private X509Certificate _serverCertificate, _clientCertificate;
        private PrivateKey _serverKey, _clientKey;

        public TlsState()
        {
            X509CertificateAuthority ca = X509CertificateAuthority.CreateX509CertificateAuthority
            (
                2048,
                "Root",
                "Root",
                DateTime.Now,
                DateTime.Now + TimeSpan.FromDays(365),
                out _
            );

            this.CreateCertificate(ca, "server", out this._serverKey, out this._serverCertificate);
            this.CreateCertificate(ca, "client", out this._clientKey, out this._clientCertificate);

            this._ca = ca;
        }

        private void CreateCertificate
        (
            X509CertificateAuthority ca,
            string name,
            out PrivateKey key,
            out X509Certificate cert
        )
        {
            DateTime start = DateTime.Now;
            DateTime end = start + TimeSpan.FromDays(365);

            //needs 2048 bits for level2 strength
            RSAKey rsaKey = new RSAKey(2048);
            using (X509CertificateRequest req = new X509CertificateRequest(rsaKey, name, name))
            {
                req.Sign(rsaKey, DigestType.SHA256);

                cert = ca.ProcessRequest(req, start, end);

                ca.Sign(cert, DigestType.SHA256);
            }

            key = rsaKey;
        }

        public void Dispose()
        {
            this._serverCertificate.Dispose();
            this._serverKey.Dispose();

            this._clientCertificate.Dispose();
            this._clientKey.Dispose();

            this._ca.Dispose();
        }
    }
}
