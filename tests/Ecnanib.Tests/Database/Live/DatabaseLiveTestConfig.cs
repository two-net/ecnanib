using System.Text.Json;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// Opt-in live tests read <c>tests/.database.json</c> (git-ignored):
/// <code>{ "connectionString": "Host=localhost;Port=5432;Database=postgres;Username=ecnanib;Password=..." }</code>
/// The role must be allowed to <c>CREATE DATABASE</c>: every test class migrates into its own throwaway database and
/// drops it afterwards. Every test skips when the file is absent, so the default <c>dotnet test</c> needs no server.
/// </summary>
public sealed class DatabaseLiveTestConfig
{
    public const string FileName = ".database.json";

    public static readonly string SkipMessage =
        $"Live test: add \"connectionString\" to tests/{FileName} (a PostgreSQL 15+ role that may CREATE DATABASE) to run.";

    public string? ConnectionString { get; init; }

    public static DatabaseLiveTestConfig? Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", FileName);
            if (File.Exists(candidate))
                return JsonSerializer.Deserialize<DatabaseLiveTestConfig>(File.ReadAllText(candidate), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            directory = directory.Parent;
        }

        return null;
    }
}
