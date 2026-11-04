using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Ecnanib.Platform.Secrets.DataProtection;

/// <summary>
/// Stores a Data Protection key's secret part in <c>secrets</c>, where the master key encrypts it and
/// rotation re-encrypts it like any other secret. The key ring keeps only a reference. The secret is
/// saved before the key ring row, so a failure between the two leaves a secret nothing refers to,
/// which is encrypted and harmless.
/// </summary>
internal sealed class SecretStoreXmlEncryptor(SecretStore secrets) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));

        try
        {
            var id = secrets.AddNow(plaintext);

            return new EncryptedXmlInfo(
                new XElement(SecretStoreXmlDecryptor.ElementName, new XAttribute(SecretStoreXmlDecryptor.IdAttribute, id)),
                typeof(SecretStoreXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
