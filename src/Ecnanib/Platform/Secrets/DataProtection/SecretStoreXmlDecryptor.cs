using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Ecnanib.Platform.Secrets.DataProtection;

/// <summary>
/// Reads back what <see cref="SecretStoreXmlEncryptor"/> stored. Data Protection writes this type's
/// assembly-qualified name into every key's XML and creates it by that name, through the constructor
/// that takes an <see cref="IServiceProvider"/>. <b>Never rename or move it, and never lower the
/// assembly version</b>, which is in that name: every stored key would become unreadable, and
/// everyone would be signed out. DataProtectionKeyTests pins the name.
/// </summary>
internal sealed class SecretStoreXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public const string ElementName = "secret";

    public const string IdAttribute = "id";

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var id = (Guid?)encryptedElement.Attribute(IdAttribute)
            ?? throw new InvalidOperationException("A Data Protection key refers to a secret without saying which.");

        using var secret = services.GetRequiredService<SecretStore>().Reveal(id);

        return XElement.Parse(secret.Reveal());
    }
}
