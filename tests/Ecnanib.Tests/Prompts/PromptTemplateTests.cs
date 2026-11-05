using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Prompts;

/// <summary>
/// FR-PRM-02 and FR-PRM-04: the first start seeds a template for each layer's own dimension, every combination
/// composes from them, an administrator's override reaches only its combination, and a restart keeps their versions.
/// </summary>
public sealed class PromptTemplateTests(PostgresFixture postgres)
{
    private const string Keys =
        "SELECT layer || coalesce(' ' || role, '') || coalesce(' ' || market, '') || coalesce(' ' || timeframe, '') || ' v' || version "
        + "FROM prompt_templates";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_start_seeds_version_1_of_each_template()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);

        app.Start();

        string[] expected =
        [
            "System v1",
            .. Enum.GetValues<AiRole>().SelectMany(role => new[] { $"OutputContract {role} v1", $"Role {role} v1" }),
            .. Enum.GetValues<Market>().Select(market => $"Market {market} v1"),
            .. Enum.GetValues<Timeframe>().Select(timeframe => $"Timeframe {timeframe} v1"),
        ];
        Assert.Equal(17, expected.Length);
        Assert.Equal(expected.Order(StringComparer.Ordinal), (await Sql.ColumnAsync(database, Keys, Cancellation)).Order(StringComparer.Ordinal));
    }

    /// <summary>FR-PRM-02: all 40 combinations of role, market and timeframe.</summary>
    [Fact]
    public async Task Every_combination_composes_a_complete_prompt_from_the_seeds()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        var prompts = app.Services.GetRequiredService<IPrompts>();

        var combinations =
            from role in Enum.GetValues<AiRole>()
            from market in Enum.GetValues<Market>()
            from timeframe in Enum.GetValues<Timeframe>()
            select (role, market, timeframe);

        foreach (var (role, market, timeframe) in combinations)
        {
            var prompt = await prompts.ComposeAsync(new PromptRequest(role, market, timeframe, PromptContext.Empty), Cancellation);

            Assert.Equal(PromptComposer.Layers, prompt.Templates.Select(template => template.Key.Layer));
            Assert.All(prompt.Templates, template => Assert.True(template.Key.Matches(role, market, timeframe)));
            Assert.All(prompt.Templates, template => Assert.Equal(1, template.Version));
            Assert.StartsWith("# System rules", prompt.Instructions, StringComparison.Ordinal);
            Assert.Contains($"\n# Market: Binance {(market == Market.Spot ? "Spot" : "USDⓈ-M Futures")}\n", prompt.Instructions, StringComparison.Ordinal);
            Assert.Equal("<runtime_context>\n</runtime_context>", prompt.Message);
        }

        Assert.Equal(40, combinations.Count());
    }

    [Fact]
    public async Task An_override_for_one_combination_reaches_only_that_combination()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        var prompts = app.Services.GetRequiredService<IPrompts>();

        await Sql.ExecuteAsync(
            database,
            "INSERT INTO prompt_templates (id, layer, role, market, timeframe, version, content, created_at) "
            + "VALUES (gen_random_uuid(), 'Timeframe', 'Strategist', 'Futures', 'FifteenMinutes', 1, 'OVERRIDE', now())",
            Cancellation);

        var overridden = await prompts.ComposeAsync(new PromptRequest(AiRole.Strategist, Market.Futures, Timeframe.FifteenMinutes, PromptContext.Empty), Cancellation);
        Assert.EndsWith("\n\nOVERRIDE", overridden.Instructions, StringComparison.Ordinal);
        Assert.Equal(
            new PromptTemplateKey(PromptLayer.Timeframe, AiRole.Strategist, Market.Futures, Timeframe.FifteenMinutes),
            overridden.Templates[^1].Key);

        foreach (var (role, market, timeframe) in new[]
        {
            (AiRole.RiskCritic, Market.Futures, Timeframe.FifteenMinutes),
            (AiRole.Strategist, Market.Spot, Timeframe.FifteenMinutes),
            (AiRole.Strategist, Market.Futures, Timeframe.OneHour),
        })
        {
            var prompt = await prompts.ComposeAsync(new PromptRequest(role, market, timeframe, PromptContext.Empty), Cancellation);

            Assert.DoesNotContain("OVERRIDE", prompt.Instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_restart_keeps_a_newer_version_and_seeds_only_a_missing_key()
    {
        var database = postgres.NewConnectionString();
        await using (var first = new AppFactory(database))
        {
            first.Start();
        }

        await Sql.ExecuteAsync(
            database,
            "INSERT INTO prompt_templates (id, layer, role, market, timeframe, version, content, created_at) "
            + "VALUES (gen_random_uuid(), 'Market', NULL, 'Spot', NULL, 2, 'SPOT V2', now())",
            Cancellation);
        await Sql.ExecuteAsync(database, "DELETE FROM prompt_templates WHERE layer = 'System'", Cancellation);

        await using var second = new AppFactory(database);
        var prompt = await second.Services.GetRequiredService<IPrompts>()
            .ComposeAsync(new PromptRequest(AiRole.Strategist, Market.Spot, Timeframe.OneDay, PromptContext.Empty), Cancellation);

        Assert.Equal(18, (await Sql.ColumnAsync(database, Keys, Cancellation)).Count);
        Assert.Contains("System v1", await Sql.ColumnAsync(database, Keys, Cancellation));
        Assert.Contains("\n\nSPOT V2\n\n", prompt.Instructions, StringComparison.Ordinal);
        Assert.Equal(2, prompt.Templates.Single(template => template.Key.Layer == PromptLayer.Market).Version);
    }

    [Theory]
    [InlineData("'Market', NULL, NULL, NULL, 2", "ck_prompt_templates_layer_dimension")]
    [InlineData("'OutputContract', NULL, NULL, NULL, 2", "ck_prompt_templates_layer_dimension")]
    [InlineData("'System', NULL, NULL, NULL, 0", "ck_prompt_templates_version")]
    [InlineData("'System', NULL, NULL, NULL, 1", "ix_prompt_templates_layer_role_market_timeframe_version")]
    public async Task The_table_refuses_a_key_without_its_dimension_a_version_below_1_and_a_second_row_for_a_version(string values, string constraint)
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(database);
        app.Start();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(
            database,
            $"INSERT INTO prompt_templates (id, layer, role, market, timeframe, version, content, created_at) VALUES (gen_random_uuid(), {values}, 'X', now())",
            Cancellation));

        Assert.Equal(constraint, exception.ConstraintName);
    }
}
