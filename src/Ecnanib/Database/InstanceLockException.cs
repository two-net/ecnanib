namespace Ecnanib.Database;

/// <summary>Another process holds the database's instance lock (NFR-REL-04); the application will not start. The cause was logged at Critical.</summary>
public sealed class InstanceLockException : Exception
{
    public InstanceLockException(string message)
        : base(message)
    {
    }
}
