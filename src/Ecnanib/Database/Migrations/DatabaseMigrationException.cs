namespace Ecnanib.Database.Migrations;

/// <summary>The schema could not be brought up to date at startup; the application refuses to start (NFR-ARCH-03).</summary>
public sealed class DatabaseMigrationException : Exception
{
    public DatabaseMigrationException(string message)
        : base(message)
    {
    }

    public DatabaseMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
