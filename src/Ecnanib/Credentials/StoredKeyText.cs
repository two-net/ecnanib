using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Security;

namespace Ecnanib.Credentials;

/// <summary>
/// Reads a stored key as text, for the one call that it is read for (FR-KEY-9). It is what
/// decrypts a key, for the two that hand one to a call: what checks a key with its provider, and
/// what hands a key to the call of a model. See docs/credentials.md.
/// </summary>
internal static class StoredKeyText
{
    /// <summary>
    /// The key of a row as text, or null when the value does not decrypt. The bytes are cleared
    /// here.
    /// </summary>
    /// <param name="secrets">What decrypts a stored secret.</param>
    /// <param name="encryptedValue">The key, as it is stored.</param>
    /// <param name="masterKeyId">The id of the master key that encrypted it.</param>
    /// <param name="boundTo">What the value is bound to: its row and its owner.</param>
    public static string? Read(StoredSecrets secrets, byte[] encryptedValue, string masterKeyId, string boundTo)
    {
        byte[] key;
        try
        {
            key = secrets.Decrypt(new EncryptedSecret(masterKeyId, encryptedValue), boundTo);
        }
        catch (CryptographicException)
        {
            // It names a master key that the file does not list, or was changed. The start of
            // the application has said so, for every such value.
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
