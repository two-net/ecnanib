using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Catalog;

/// <summary>CFG-03: the Catalog section stops startup with a message that names what's wrong.</summary>
public sealed class CatalogSettingsTests(PostgresFixture postgres)
{
    private const string Interval = "Catalog:SyncIntervalHours";
    private const string Entry = "Catalog:Overrides:0";

    /// <summary>Each case is settings as key=value, and the key the message must name.</summary>
    public static TheoryData<string[], string> InvalidSettings => new()
    {
        { [$"{Interval}=0"], Interval },
        { [$"{Interval}=169"], Interval },
        { [$"{Interval}=twelve"], Interval },
        { [$"{Entry}:Model=gpt-6-luna"], $"{Entry}:Provider" },
        { [$"{Entry}:Provider=openai", $"{Entry}:Model=gpt-6-luna"], $"{Entry}:Provider" },
        { [$"{Entry}:Provider=OpenAI"], $"{Entry}:Model" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt 6"], $"{Entry}:Model" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-*"], $"{Entry}:Model" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:InputPricePerMillion=-1", $"{Entry}:OutputPricePerMillion=2"], $"{Entry}:InputPricePerMillion" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:InputPricePerMillion=1"], $"{Entry}:OutputPricePerMillion" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:ContextWindow=0"], $"{Entry}:ContextWindow" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:ImageInput=yes"], $"{Entry}:ImageInput" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:DisplayName={new string('x', 101)}"], $"{Entry}:DisplayName" },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", $"{Entry}:InputPrice=1"], $"{Entry}:InputPrice " },
        { [$"{Entry}:Provider=OpenAI", $"{Entry}:Model=gpt-6-luna", "Catalog:Overrides:1:Provider=OpenAI", "Catalog:Overrides:1:Model=gpt-6-luna"], "Catalog:Overrides:1" },
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string[] settings, string named)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: Parse(settings));

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_setting_stops_startup_before_any_migration()
    {
        var database = await postgres.CreateDatabaseAsync(Cancellation);
        await using var app = new AppFactory(database, settings: Parse([$"{Interval}=0"]));

        Assert.ThrowsAny<Exception>(app.Start);

        Assert.Equal(0, await Sql.ScalarAsync<long>(
            database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", Cancellation));
    }

    [Fact]
    public async Task The_shipped_settings_and_none_at_all_both_start()
    {
        await using var shipped = new AppFactory(postgres.NewConnectionString(), settings: Parse([$"{Interval}=12"]));
        await using var none = new AppFactory(postgres.NewConnectionString());

        shipped.Start();
        none.Start();
    }

    private static Dictionary<string, string?> Parse(string[] settings) =>
        settings.Select(setting => setting.Split('=', 2)).ToDictionary(pair => pair[0], pair => (string?)pair[1]);
}
