namespace Ecnanib.Database;

/// <summary>
/// A stored user key does not open under the configured key, or the start-up step that proves the key and encrypts
/// the rows failed (FR-KEY-04); the application will not start. The cause was logged at Critical.
/// </summary>
public sealed class StoredKeyProtectionException : Exception
{
    public StoredKeyProtectionException(string message)
        : base(message)
    {
    }

    public StoredKeyProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
