using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LiteLink.Transport
{
    /// <summary>
    /// Generates the self-signed certificates the TCP and QUIC servers fall back to when no
    /// certificate is configured. Meant for development and internal setups — clients have to
    /// disable certificate validation (e.g. AllowInvalidCertificates) to accept them.
    /// </summary>
    internal static class SelfSignedCertificate
    {
        public static X509Certificate2 Create()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=LiteLink self-signed", key, HashAlgorithmName.SHA256);

            //serverAuth EKU, required by Schannel for QUIC certificates
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            try
            {
                var hostName = Dns.GetHostName();
                if (!string.IsNullOrEmpty(hostName) && !string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
                    san.AddDnsName(hostName);
            }
            catch (Exception) { } //no resolvable host name, localhost + loopback still work
            request.CertificateExtensions.Add(san.Build());

            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            //Schannel can't use ephemeral private keys, round-trip through PFX to persist the key
            var pfx = cert.Export(X509ContentType.Pfx);
            cert.Dispose();
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
    }
}
