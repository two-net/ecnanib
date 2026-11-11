namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Database</c> section: the connection to the one PostgreSQL database.</summary>
public sealed record DatabaseSettings(Secret ConnectionString)
{
    internal static DatabaseSettings Read(SettingsReader file) =>
        new(file.SecretText("Database:ConnectionString"));
}
