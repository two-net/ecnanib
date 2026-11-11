namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>ConnectionStrings</c> section, where a .NET application keeps its connection strings:
/// the connection to the one PostgreSQL database, under the name of its context.
/// </summary>
public sealed record ConnectionStringsSettings(Secret EcnanibDbContext)
{
    internal static ConnectionStringsSettings Read(SettingsReader file) =>
        new(file.ConnectionString("ConnectionStrings:EcnanibDbContext"));
}
