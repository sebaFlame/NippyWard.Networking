using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net;

namespace NippyWard.Networking.Transports.Proxy
{
    internal class ProxyCredential : ICredentials
    {
        private readonly Uri _proxyUri;
        private readonly NetworkCredential? _networkCredential;

        private ProxyCredential
        (
            Uri proxyUri,
            NetworkCredential? networkCredential
        )
        {
            this._proxyUri = proxyUri;
            this._networkCredential = networkCredential;
        }

        public static ProxyCredential TryCreate(Uri uri)
        {
            NetworkCredential? networkCredential
                = GetCredentialsFromString(uri.UserInfo);

            return new ProxyCredential(uri, networkCredential);
        }

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if(uri is null)
            {
                return null;
            }

            return uri.Equals(this._proxyUri)
                ? this._networkCredential
                : null;
        }

        private static NetworkCredential? GetCredentialsFromString
        (
            string? value
        )
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value == ":")
            {
                return CredentialCache.DefaultNetworkCredentials;
            }

            value = Uri.UnescapeDataString(value);

            string password = "";
            string? domain = null;
            int idx = value.IndexOf(':');
            if (idx != -1)
            {
                password = value.Substring(idx + 1);
                value = value.Substring(0, idx);
            }

            idx = value.IndexOf('\\');
            if (idx != -1)
            {
                domain = value.Substring(0, idx);
                value = value.Substring(idx + 1);
            }

            return new NetworkCredential(value, password, domain);
        }
    }
}
