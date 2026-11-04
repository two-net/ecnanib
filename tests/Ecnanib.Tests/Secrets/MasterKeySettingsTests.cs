using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Secrets;

/// <summary>NFR-SEC-05 and CFG-03, for Security:EncryptionKeys.</summary>
public sealed class MasterKeySettingsTests(PostgresFixture postgres)
{
    private const string Section = TestKeys.Section;

    private static readonly string Good = TestKeys.NewKey();
    private static readonly string Other = TestKeys.NewKey();

    public static TheoryData<string, Dictionary<string, string?>> InvalidSettings => new()
    {
        { $"{Section} needs at least one key", new() { [Section] = null } },
        { $"{Section} must be a list", new() { [$"{Section}:Id"] = "a", [$"{Section}:Key"] = Good, [$"{Section}:Active"] = "true" } },
        { $"{Section} must mark exactly one key Active, and it marks 0", TestKeys.Settings(("a", Good, false)) },
        { $"{Section} must mark exactly one key Active, and it marks 2", TestKeys.Settings(("a", Good, true), ("b", Other, true)) },
        { $"""{Section}, entry 0 (Id "a"), must have Active""", Merge(TestKeys.Settings(("a", Good, true)), $"{Section}:0:Active", "perhaps") },
        { $"{Section}, entry 0, needs an Id", Merge(TestKeys.Settings(("a", Good, true)), $"{Section}:0:Id", null) },
        { $"{Section}, entry 0, has an Id that isn't", TestKeys.Settings(("two words", Good, true)) },
        { $"{Section}, entry 0, has an Id that isn't", TestKeys.Settings((new string('a', 33), Good, true)) },
        { $"{Section}, entries 0 and 1, have the same Id", TestKeys.Settings(("a", Good, true), ("A", Other, false)) },
        { $"""{Section}, entry 0 (Id "a"), needs a Key""", Merge(TestKeys.Settings(("a", Good, true)), $"{Section}:0:Key", null) },
        { $"""{Section}, entry 0 (Id "a"), has the sample Key""", TestKeys.Settings(("a", "<base64-encoded 256-bit key>", true)) },
        { $"""{Section}, entry 0 (Id "a"), needs a Key of 32 random bytes""", TestKeys.Settings(("a", "not base64 at all", true)) },
        { $"""{Section}, entry 0 (Id "a"), needs a Key of 32 random bytes""", TestKeys.Settings(("a", Convert.ToBase64String(new byte[16]), true)) },
        { $"""{Section}, entry 0 (Id "a"), needs a Key of 32 random bytes""", TestKeys.Settings(("a", Convert.ToBase64String(new byte[64]), true)) },
        { $"""{Section}, entry 0 (Id "a"), has a Key that isn't random""", TestKeys.Settings(("a", Convert.ToBase64String(new byte[32]), true)) },
        { $"{Section}, entries 0 and 1, have the same Key", TestKeys.Settings(("a", Good, true), ("b", Good, false)) },
        { $"""{Section}, entry 0, has a setting called "Kye", which isn't one""", Merge(TestKeys.Settings(("a", Good, true)), $"{Section}:0:Kye", Other) },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_key_stops_startup_and_is_named(string named, Dictionary<string, string?> settings)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: settings);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A key that can't be read must not get as far as changing the schema.</summary>
    [Fact]
    public async Task An_invalid_key_stops_startup_before_any_migration()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);

        await using var app = new AppFactory(database, settings: TestKeys.Settings(("a", Good, false)));

        Assert.ThrowsAny<Exception>(app.Start);
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", cancellation));
    }

    [Fact]
    public async Task Several_keys_with_one_active_start()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: TestKeys.Settings(("2026-05", Other, false), ("2026-11", Good, true)));

        app.Start();
    }

    private static Dictionary<string, string?> Merge(Dictionary<string, string?> settings, string key, string? value)
    {
        settings[key] = value;
        return settings;
    }
}
