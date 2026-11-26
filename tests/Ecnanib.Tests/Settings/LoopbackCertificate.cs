using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// The certificate of an application that listens on a port of this machine for a test that
/// drives a browser. The application serves nothing over plain HTTP, and its cookies are sent
/// only over TLS, so such an application answers over TLS too. The certificate is made for the
/// run, signed by itself, and kept nowhere: it depends on nothing that the machine has, and
/// whoever asks the application is told to take it, as the browser of such a test is.
/// </summary>
internal static class LoopbackCertificate
{
    // The purpose of a certificate that a server answers with.
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>A certificate for <c>127.0.0.1</c> and <c>localhost</c>, good from yesterday for a month.</summary>
    public static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();

        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false));

        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Written out and read again, so the key is one that a server can answer with on every
        // system: a key that was only ever in memory is not, on some.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), password: null);
    }
}
