using System.Text.Json;
using Ecnanib.Catalog;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// Only eligible models can be selected for agents. Which models are eligible is set in the
/// configuration file, with allow and deny patterns for each provider, and so is which of them
/// can search live, because providers do not report what their models can do in one way
/// (FR-CAT-6). A model that its provider no longer offers cannot be selected, and nothing is
/// put in its place (FR-CAT-8).
/// </summary>
/// <remarks>
/// The file of the tests allows the models of Claude that <c>claude-*</c> names, but for those
/// that <c>*-preview</c> names, and allows no model of another provider. It lets a model miss 3
/// syncs. A test that needs other patterns writes them into its own file.
/// </remarks>
public class ModelEligibilityTests
{
    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 20, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");
    private static readonly AiProviderCode Gemini = new("gemini");
    private static readonly AiProviderCode Grok = new("grok");

    [Fact]
    public async Task Only_a_model_that_an_allow_pattern_names_and_no_deny_pattern_names_can_be_selected()
    {
        // The provider lists a model that the file allows, one that it allows and denies, and
        // one that no pattern names: a model that writes no text, as providers list them too.
        var providers = new StandInModelLists
        {
            Answer = (_, _) => StandInModelLists.Listed(
                StandInModelLists.Model("claude-opus-5-5", "Claude Opus 5.5"),
                StandInModelLists.Model("claude-next-preview"),
                StandInModelLists.Model("text-embedding-9")),
        };
        using var application = await WithAKeyForClaude(providers).StartedAsync();

        // The catalog holds all three: what a provider lists is stored whatever the file says.
        var stored = StoredAiModel.Of(application.Database, "claude");
        Assert.Equal(["claude-next-preview", "claude-opus-5-5", "text-embedding-9"], stored.Select(model => model.Identifier));

        // One of them can be selected, and it is the row of the catalog, with what the provider said.
        var opus = stored.Single(model => model.Identifier == "claude-opus-5-5");
        var selectable = Assert.Single(await application.Selectable.ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new SelectableModel(opus.Id, Claude, "claude-opus-5-5", "Claude Opus 5.5", LiveSearch: false), selectable);

        // Asked for by its id, a model answers the same way.
        Assert.Equal(selectable, await application.Selectable.FindAsync(opus.Id, TestContext.Current.CancellationToken));

        foreach (var other in stored.Where(model => model.Id != opus.Id))
        {
            Assert.Null(await application.Selectable.FindAsync(other.Id, TestContext.Current.CancellationToken));
        }

        // And an id that the catalog does not hold is no model at all.
        Assert.Null(await application.Selectable.FindAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Theory]

    // A star stands for any run of characters, an empty one included, wherever it is.
    [InlineData("claude-*", new[] { "claude-", "claude-a", "claude-a-b" }, new[] { "claude", "xclaude-a" })]
    [InlineData("*-mini", new[] { "-mini", "a-mini", "a-b-mini" }, new[] { "a-mini-2", "mini" })]
    [InlineData("claude-*-latest", new[] { "claude--latest", "claude-opus-latest" }, new[] { "claude-latest", "claude-opus-latest-2" })]
    [InlineData("*", new[] { "a", "claude-opus-5-5" }, new string[0])]

    // Several of them, and what follows one is found wherever it is.
    [InlineData("*o*u*", new[] { "claude-opus", "sonnet-ultra" }, new[] { "haiku", "ultra-sonnet" })]
    [InlineData("a*b*c", new[] { "abc", "a-b-b-c", "abcbc" }, new[] { "a-b-c-", "ab", "acb" })]

    // Without one, a pattern names the model whose identifier it is, and no other.
    [InlineData("claude-opus-5-5", new[] { "claude-opus-5-5" }, new[] { "claude-opus-5-5-fast", "the-claude-opus-5-5", "claude-opus-5" })]

    // Nothing else is special: a dot, a question mark, and a bracket are themselves.
    [InlineData("gpt-5.5?", new[] { "gpt-5.5?" }, new[] { "gpt-5x5a", "gpt-5.5", "gpt-5.5ab" })]
    [InlineData("o[1-3]", new[] { "o[1-3]" }, new[] { "o1", "o2" })]

    // Letter case is ignored, as it is where the file is held to naming a pattern once.
    [InlineData("CLAUDE-*", new[] { "Claude-B", "claude-a" }, new[] { "cloud-a" })]
    public async Task A_star_stands_for_any_run_of_characters_and_nothing_else_is_special(
        string pattern, string[] named, string[] others)
    {
        var providers = new StandInModelLists
        {
            Answer = (_, _) => StandInModelLists.Listed([.. named.Concat(others).Select(id => StandInModelLists.Model(id))]),
        };
        using var application = await WithAKeyForClaude(providers, file => file
            .WithJson("AiProviders:Claude:Allow", JsonSerializer.Serialize(new[] { pattern }))
            .WithJson("AiProviders:Claude:Deny", "[]")).StartedAsync();

        var selectable = await application.Selectable.ListAsync(TestContext.Current.CancellationToken);

        // By identifier, in the order of its characters.
        Assert.Equal(named.Order(StringComparer.Ordinal), selectable.Select(model => model.Identifier));
    }

    [Fact]
    public async Task A_provider_without_an_allow_pattern_offers_no_model()
    {
        // The file of the tests allows nothing of Gemini: its list of patterns is empty. Its
        // catalog is filled with the key of a user, as the catalog of a provider is that the
        // service has no key for.
        var database = PostgresServer.CreateDatabase();

        using (var application = await new CatalogApplication(new TestClock(Start), database).StartedAsync())
        {
            await application.Catalog.KeyValidatedAsync(Gemini, "a-key-for-gemini", TestContext.Current.CancellationToken);

            var stored = StoredAiModel.Of(database, "gemini");
            Assert.Equal(["gemini-test-model", "gemini-test-model-mini"], stored.Select(model => model.Identifier));

            // A model is selected because the file allows it, and never because it denies nothing.
            Assert.Empty(await application.Selectable.ListAsync(TestContext.Current.CancellationToken));

            foreach (var model in stored)
            {
                Assert.Null(await application.Selectable.FindAsync(model.Id, TestContext.Current.CancellationToken));
            }
        }

        // The same catalog, with a file that allows the models of Gemini but for one.
        using var allowing = await new CatalogApplication(
            new TestClock(Start),
            database,
            settings: file => file
                .WithJson("AiProviders:Gemini:Allow", """[ "gemini-*" ]""")
                .WithJson("AiProviders:Gemini:Deny", """[ "*-mini" ]""")).StartedAsync();

        // What the file says of a model holds from the start of the application, with no sync.
        Assert.Empty(allowing.Providers.Asked);
        Assert.Equal(
            ["gemini-test-model"],
            (await allowing.Selectable.ListAsync(TestContext.Current.CancellationToken)).Select(model => model.Identifier));
    }

    [Fact]
    public async Task A_model_that_is_unavailable_cannot_be_selected_until_it_reappears_and_nothing_takes_its_place()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists
        {
            Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-opus-5-5"), StandInModelLists.Model("claude-haiku-4-5")),
        };
        using var application = await WithAKeyForClaude(providers, clock: clock).StartedAsync();

        var both = await application.Selectable.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["claude-haiku-4-5", "claude-opus-5-5"], both.Select(model => model.Identifier));
        var haiku = both[0];

        // The provider stops listing one of the two, and lists a newer one that the file allows
        // just as well. The third sync that lacks the model marks it unavailable.
        providers.Answer = (_, _) => StandInModelLists.Listed(
            StandInModelLists.Model("claude-opus-5-5"), StandInModelLists.Model("claude-haiku-5"));

        foreach (var minutes in new[] { 10, 20 })
        {
            clock.Set(Start.AddMinutes(minutes));
            await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

            // Missed, and still active: it can be selected until it is taken for gone.
            Assert.Equal(haiku, await application.Selectable.FindAsync(haiku.Id, TestContext.Current.CancellationToken));
        }

        clock.Set(Start.AddMinutes(30));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Unavailable", StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").Status);
        Assert.Null(await application.Selectable.FindAsync(haiku.Id, TestContext.Current.CancellationToken));

        // The newer model can be selected as a model of its own, under an id of its own. The
        // one that is gone is not it: nothing answers for the id that was selected (FR-CAT-8).
        var now = await application.Selectable.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["claude-haiku-5", "claude-opus-5-5"], now.Select(model => model.Identifier));
        Assert.DoesNotContain(now, model => model.Id == haiku.Id);
        Assert.Equal(both[1], now[1]);

        // The provider lists it again: it is active again, and can be selected as it could.
        providers.Answer = (_, _) => StandInModelLists.Listed(
            StandInModelLists.Model("claude-opus-5-5"), StandInModelLists.Model("claude-haiku-4-5"));
        clock.Set(Start.AddMinutes(40));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(haiku, await application.Selectable.FindAsync(haiku.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Live_search_is_said_of_a_model_and_keeps_none_from_being_selected()
    {
        // The file says of some of the models of Grok that they can search live, and of one
        // that it does not allow.
        var providers = new StandInModelLists
        {
            Answer = (provider, _) => provider == "grok"
                ? StandInModelLists.Listed(
                    StandInModelLists.Model("grok-4-fast"), StandInModelLists.Model("grok-3"), StandInModelLists.Model("aurora-4-image"))
                : StandInModelLists.Listed(StandInModelLists.Model("claude-opus-5-5")),
        };
        using var application = await WithAKeyForClaude(providers, file => file
            .WithJson("AiProviders:Grok:Allow", """[ "grok-*" ]""")
            .WithJson("AiProviders:Grok:LiveSearch", """[ "grok-4*", "aurora-*" ]""")).StartedAsync();

        await application.Catalog.KeyValidatedAsync(Grok, "a-key-for-grok", TestContext.Current.CancellationToken);

        var selectable = await application.Selectable.ListAsync(TestContext.Current.CancellationToken);

        // By provider, and then by identifier. A model that cannot search live is selected like
        // any other (FR-CYC-3), and one that the file does not allow is not selected for it.
        Assert.Equal(
            [("claude", "claude-opus-5-5", false), ("grok", "grok-3", false), ("grok", "grok-4-fast", true)],
            selectable.Select(model => (model.Provider.Value, model.Identifier, model.LiveSearch)));

        // What calls a model is told the same as a selector shows, of every model that can be
        // selected: which models have live search comes from the flags of the file (FR-CYC-3).
        var capabilities = application.Capabilities;
        Assert.All(selectable, model => Assert.Equal(model.LiveSearch, capabilities.SearchesLive(model.Provider, model.Identifier)));

        // It goes by the file alone, and by the patterns of the model's own provider: letter
        // case is ignored, as in every pattern, and a model that is in no catalog is answered too.
        Assert.True(capabilities.SearchesLive(Grok, "GROK-4.7"));
        Assert.True(capabilities.SearchesLive(Grok, "aurora-4-image"));
        Assert.False(capabilities.SearchesLive(Claude, "grok-4-fast"));
        Assert.False(capabilities.SearchesLive(new AiProviderCode("unheard-of"), "grok-4-fast"));
    }

    private static CatalogApplication WithAKeyForClaude(
        StandInModelLists providers, Func<SettingsJson, SettingsJson>? settings = null, TestClock? clock = null) =>
        new(
            clock ?? new TestClock(Start),
            providers: providers,
            settings: file => settings is null ? file.WithCatalogKey() : settings(file.WithCatalogKey()));
}
