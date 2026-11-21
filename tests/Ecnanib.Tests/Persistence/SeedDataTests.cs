using Ecnanib.AiProviderGateway;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Roles, providers, and prompt templates are seeded through migrations (NFR-DAT-10): they are in
/// a database that has had the migrations applied, and nothing else done to it.
/// </summary>
public class SeedDataTests
{
    private static readonly Lazy<TestDatabase> Migrated = new(() =>
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        return database;
    });

    [Fact]
    public void The_roles_are_the_four_roles_of_a_team()
    {
        using var context = Migrated.Value.Context();

        var roles = context.Set<RoleRow>().AsNoTracking().ToList();

        Assert.Equal(
            new Dictionary<Role, string>
            {
                [Role.MarketAnalyst] = "Market Analyst",
                [Role.SentimentAnalyst] = "Sentiment Analyst",
                [Role.Strategist] = "Strategist",
                [Role.RiskManager] = "Risk Manager",
            },
            roles.ToDictionary(role => role.Code, role => role.DisplayName));

        // Every member of the enum, so the table cannot fall behind the code.
        Assert.Equal(Enum.GetValues<Role>().Order(), roles.Select(role => role.Code).Order());
    }

    [Fact]
    public void The_providers_are_the_ones_the_tracked_configuration_file_names()
    {
        using var context = Migrated.Value.Context();

        var providers = context.Set<AiProvider>().AsNoTracking().ToList();

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["claude"] = "Claude",
                ["gemini"] = "Gemini",
                ["gpt"] = "GPT",
                ["grok"] = "Grok",
            },
            providers.ToDictionary(provider => provider.Code.Value, provider => provider.DisplayName));

        Assert.Equal(
            TrackedSettings().AiProviders.ByCode.Keys.Select(code => code.Value).Order(),
            providers.Select(provider => provider.Code.Value).Order());
    }

    [Fact]
    public void There_is_one_template_in_use_for_each_role_market_timeframe_and_profile_and_one_for_the_lesson()
    {
        using var context = Migrated.Value.Context();

        var templates = context.Set<PromptTemplate>().AsNoTracking().ToList();

        // The templates in use, whatever versions came before them: an edit adds a version,
        // and the earlier one stays stored (FR-PRM-6). PromptTemplateVersionTests has the rule
        // of the versions.
        var inUse = templates.Where(template => template.IsActive).ToList();

        AssertSubjects(PromptTemplateKind.Role, Enum.GetNames<Role>());
        AssertSubjects(PromptTemplateKind.Market, Enum.GetNames<Market>());
        AssertSubjects(PromptTemplateKind.Timeframe, ["15m", "1H", "4H", "1D"]);
        AssertSubjects(PromptTemplateKind.Profile, ["Aggressive"]);

        // And the instructions for the lesson of a closed position, which are the same for
        // every agent (FR-EXP-6).
        AssertSubjects(PromptTemplateKind.Lesson, ["PostTrade"]);

        Assert.Equal(12, inUse.Count);
        Assert.All(templates, template =>
        {
            Assert.True(template.Version >= 1);
            Assert.False(string.IsNullOrWhiteSpace(template.Text));
        });

        // A version that is out of use is an earlier one of a template that is in use.
        Assert.All(templates.Where(template => !template.IsActive), earlier =>
            Assert.Contains(inUse, latest =>
                latest.Kind == earlier.Kind && latest.Subject == earlier.Subject && latest.Version > earlier.Version));

        void AssertSubjects(PromptTemplateKind kind, string[] subjects) =>
            Assert.Equal(
                subjects.Order(StringComparer.Ordinal),
                inUse.Where(template => template.Kind == kind).Select(template => template.Subject).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_kind_and_subject_cannot_have_two_templates_in_use()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        // The next version of a template, whichever version the seed has it in, stored in use
        // beside the one that is.
        const string Newer =
            """
            INSERT INTO prompt_templates (id, kind, subject, version, text, is_active)
            SELECT (SELECT max(id) + 1 FROM prompt_templates), 'Market', 'Spot', max(version) + 1, 'Newer', true
            FROM prompt_templates WHERE kind = 'Market' AND subject = 'Spot'
            """;

        var rejected = Assert.Throws<Npgsql.PostgresException>(() => database.Execute(Newer));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, rejected.SqlState);
        Assert.Equal("ix_prompt_templates_kind_subject", rejected.ConstraintName);

        // With the earlier version taken out of use, the newer one is accepted: that is how a
        // new version arrives.
        database.Execute("UPDATE prompt_templates SET is_active = false WHERE kind = 'Market' AND subject = 'Spot'");
        database.Execute(Newer);
    }

    /// <summary>The settings in the tracked configuration file, which the test project copies into its output.</summary>
    private static EcnanibSettings TrackedSettings()
    {
        using var directory = new SettingsDirectory(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json")));

        return SettingsFile.Load(directory.SettingsPath);
    }
}
