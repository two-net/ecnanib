using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// FR-ROLE-03, NFR-DEP-02 and CFG-03: the RoleDefaults section names a provider per role, and optionally a
/// model, stops startup with a message that names what's wrong, and seeds role_defaults once per role.
/// </summary>
public sealed class RoleDefaultTests(PostgresFixture postgres)
{
    private const string Section = "RoleDefaults";
    private const string Rows = "SELECT role || ' ' || provider || coalesce(' ' || model_id, '') FROM role_defaults ORDER BY role";

    /// <summary>Each case is settings as key=value, and the key the message must name.</summary>
    public static TheoryData<string[], string> InvalidSettings => new()
    {
        { [$"{Section}:Trader=OpenAI"], $"{Section}:Trader" },
        { [$"{Section}:strategist=OpenAI"], $"{Section}:strategist" },
        { [$"{Section}:0=OpenAI"], $"{Section}:0" },
        { [$"{Section}:Strategist=openai"], $"{Section}:Strategist" },
        { [$"{Section}:Strategist=Mistral"], $"{Section}:Strategist" },
        { [$"{Section}:Strategist:Model=gpt-6"], $"{Section}:Strategist:Provider" },
        { [$"{Section}:Strategist:Provider=Mistral"], $"{Section}:Strategist:Provider" },
        { [$"{Section}:Strategist:Provider=OpenAI", $"{Section}:Strategist:Models=gpt-6"], $"{Section}:Strategist:Models" },
        { [$"{Section}:Strategist:Provider=OpenAI", $"{Section}:Strategist:Model=gpt 6"], $"{Section}:Strategist:Model" },
        { [$"{Section}:Strategist:Provider=OpenAI", $"{Section}:Strategist:Model= gpt-6"], $"{Section}:Strategist:Model" },
        { [$"{Section}:Strategist:Provider=OpenAI", $"{Section}:Strategist:Model={new string('x', 257)}"], $"{Section}:Strategist:Model" },
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>SRS 7.3's defaults, with one role in the form that names a model too.</summary>
    private static Dictionary<string, string?> Shipped => new()
    {
        [$"{Section}:TechnicalAnalyst"] = "Google",
        [$"{Section}:SentimentAnalyst"] = "xAI",
        [$"{Section}:Strategist"] = "OpenAI",
        [$"{Section}:RiskCritic:Provider"] = "Anthropic",
        [$"{Section}:RiskCritic:Model"] = "claude-opus-5-5",
        [$"{Section}:Reviewer"] = "Google",
    };

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
        await using var app = new AppFactory(database, settings: Parse([$"{Section}:Strategist=Mistral"]));

        Assert.ThrowsAny<Exception>(app.Start);

        Assert.Equal(0, await Sql.ScalarAsync<long>(
            database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", Cancellation));
    }

    /// <summary>A role that's missing or blank has no default, and an object with no model is a provider alone.</summary>
    [Fact]
    public async Task The_first_start_seeds_each_configured_role_with_the_providers()
    {
        var database = postgres.NewConnectionString();
        var settings = Shipped;
        settings.Remove($"{Section}:Reviewer");
        settings[$"{Section}:SentimentAnalyst"] = "";
        settings[$"{Section}:Strategist"] = null;
        settings[$"{Section}:Strategist:Provider"] = "OpenAI";
        settings[$"{Section}:Strategist:Model"] = "";
        await using var app = new AppFactory(database, settings: settings);

        app.Start();

        Assert.Equal(
            ["RiskCritic Anthropic claude-opus-5-5", "Strategist OpenAI", "TechnicalAnalyst Google"],
            await Sql.ColumnAsync(database, Rows, Cancellation));
        Assert.Equal(
            [
                new RoleDefault(AiRole.TechnicalAnalyst, "Google", null),
                new RoleDefault(AiRole.Strategist, "OpenAI", null),
                new RoleDefault(AiRole.RiskCritic, "Anthropic", "claude-opus-5-5"),
            ],
            await app.Services.GetRequiredService<ICatalog>().ListRoleDefaultsAsync(Cancellation));
    }

    /// <summary>
    /// NFR-DEP-02: a seeded role keeps what administrators made of it (#26), and a role configured later is
    /// added. The section only reaches a role that has no row.
    /// </summary>
    [Fact]
    public async Task A_restart_keeps_an_administrators_edit_and_adds_a_newly_configured_role()
    {
        var database = postgres.NewConnectionString();
        var first = Shipped;
        first.Remove($"{Section}:Reviewer");

        await using (var app = new AppFactory(database, settings: first))
        {
            app.Start();
        }

        await Sql.ExecuteAsync(database, "UPDATE role_defaults SET provider = 'Google', model_id = 'gemini-9' WHERE role = 'Strategist'", Cancellation);

        await using (var app = new AppFactory(database, settings: Shipped))
        {
            app.Start();
        }

        Assert.Equal(
            [
                "Reviewer Google",
                "RiskCritic Anthropic claude-opus-5-5",
                "SentimentAnalyst xAI",
                "Strategist Google gemini-9",
                "TechnicalAnalyst Google",
            ],
            await Sql.ColumnAsync(database, Rows, Cancellation));
    }

    [Fact]
    public async Task No_section_starts_and_seeds_nothing()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);

        app.Start();

        Assert.Empty(await Sql.ColumnAsync(database, Rows, Cancellation));
    }

    private static Dictionary<string, string?> Parse(string[] settings) =>
        settings.Select(setting => setting.Split('=', 2)).ToDictionary(pair => pair[0], pair => (string?)pair[1]);
}
