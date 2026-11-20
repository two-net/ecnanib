using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// A catalog screen lists models by provider with status, metadata, last sync time, and a link
/// to each model's experience record (FR-CAT-10). Metadata that a provider does not expose is
/// shown as unknown, and is not guessed (FR-CAT-4).
/// </summary>
/// <remarks>
/// The file of the tests syncs every 12 hours and lets a model miss 3 syncs. It allows the
/// models of Claude that <c>claude-*</c> names, but for those that <c>*-preview</c> names, and no
/// model of another provider, and says of the models of Grok that they can search live. It holds
/// no catalog key: a test that syncs with the service's key adds one. The syncs that a test asks
/// for are within the hour after the start: the schedule adds none of its own before 12 hours
/// have passed, and the session of the user who reads the screen ends after 45 idle minutes.
/// </remarks>
public class CatalogPageTests
{
    private const string Ada = "ada@example.com";

    private const string CatalogPage = WebUiModule.CatalogPath;
    private const string GeminiKey = "gemini-catalog-key";
    private const string GrokKey = "grok-catalog-key";

    private const string NotSynced = "Not synced yet.";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 15, 9, 20, 0, TimeSpan.Zero);

    private static readonly string[] Columns =
    [
        "Name", "Identifier", "Status", "For agents", "Live search", "Context", "Output", "Released", "Deprecated", "Retires",
        "Experience",
    ];

    private static readonly ListedAiModel Opus = StandInModelLists.Model(
        "claude-opus-5-5",
        "Claude Opus 5.5",
        contextTokens: 1_000_000,
        outputTokens: 64_000,
        releasedAt: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));

    private static readonly ListedAiModel Haiku = StandInModelLists.Model(
        "claude-haiku-4-5",
        "Claude Haiku 4.5",
        contextTokens: 200_000,
        outputTokens: 64_000,
        releasedAt: new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero),
        deprecatedAt: new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero),
        retiresAt: new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_screen_lists_the_models_by_provider_with_status_metadata_and_last_sync_time()
    {
        using var application = new AccountsApplication(
            clock: new TestClock(Morning),
            settings: file => file.WithCatalogKey().With("AiProviders:Gemini:CatalogKey", GeminiKey));
        application.ModelLists.Answer = (provider, _) => provider == "claude"
            ? StandInModelLists.Listed(Opus, Haiku)
            : StandInModelLists.Listed(StandInModelLists.Model("gemini-3-pro", "Gemini 3 Pro", contextTokens: 2_000_000));
        using var client = await SignedInAsync(application);

        var page = KeyBrowser.Read(await client.PageAsync(CatalogPage));

        Assert.Contains("<h1>Model catalog</h1>", page);

        // Every provider has its place, in the order of the codes, whether it has models or not.
        Assert.Equal(["Claude", "Gemini", "GPT", "Grok"], CatalogBrowser.ProvidersOn(page));

        // The models of a provider are under its name, by identifier, with when they were synced.
        var claude = CatalogBrowser.SectionOf(page, "Claude");

        Assert.Contains("Last synced 15 November 2026, 09:20 UTC.", claude);
        Assert.Equal(Columns, CatalogBrowser.ColumnsOf(claude));
        Assert.Equal(
            [
                [
                    "Claude Haiku 4.5", "claude-haiku-4-5", "Active", "Yes", "No", "200,000 tokens", "64,000 tokens",
                    "15 October 2025", "1 August 2026", "1 February 2027", "Experience of Claude Haiku 4.5",
                ],
                [
                    "Claude Opus 5.5", "claude-opus-5-5", "Active", "Yes", "No", "1,000,000 tokens", "64,000 tokens",
                    "21 September 2026", "Unknown", "Unknown", "Experience of Claude Opus 5.5",
                ],
            ],
            CatalogBrowser.ModelsOf(claude));

        var gemini = CatalogBrowser.SectionOf(page, "Gemini");

        Assert.Contains("Last synced 15 November 2026, 09:20 UTC.", gemini);
        Assert.Equal(
            [
                [
                    "Gemini 3 Pro", "gemini-3-pro", "Active", "No", "No", "2,000,000 tokens", "Unknown", "Unknown", "Unknown",
                    "Unknown", "Experience of Gemini 3 Pro",
                ],
            ],
            CatalogBrowser.ModelsOf(gemini));

        // A model is on the screen once, under its own provider.
        Assert.DoesNotContain("gemini-3-pro", claude);
        Assert.DoesNotContain("claude-", gemini);
    }

    [Fact]
    public async Task What_a_provider_does_not_say_about_a_model_is_shown_as_unknown()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning), settings: file => file.WithCatalogKey());

        // The provider names the model, and says nothing more about it.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-next"));
        using var client = await SignedInAsync(application);

        var page = KeyBrowser.Read(await client.PageAsync(CatalogPage));
        var models = CatalogBrowser.ModelsOf(CatalogBrowser.SectionOf(page, "Claude"));

        // Its name and everything else that the provider could have said is unknown. What the
        // catalog knows by itself is there: the identifier, the status, and what the file says.
        Assert.Equal(
            [
                [
                    "Unknown", "claude-next", "Active", "Yes", "No", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown",
                    "Experience of claude-next",
                ],
            ],
            models);

        // Nothing is left blank: a blank would read as "none".
        Assert.All(models.SelectMany(model => model), cell => Assert.NotEqual(string.Empty, cell));
    }

    [Fact]
    public async Task A_provider_that_was_never_synced_says_so_and_lists_no_model()
    {
        // No key of the service, and no key of a user yet: nobody was asked.
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = await SignedInAsync(application);

        var empty = KeyBrowser.Read(await client.PageAsync(CatalogPage));

        Assert.Equal(["Claude", "Gemini", "GPT", "Grok"], CatalogBrowser.ProvidersOn(empty));
        Assert.All(CatalogBrowser.ProvidersOn(empty), provider =>
        {
            var section = CatalogBrowser.SectionOf(empty, provider);

            Assert.Contains(NotSynced, section);
            Assert.Contains($"No model of {provider} is listed yet.", section);
            Assert.DoesNotContain("<table", section);
        });

        // The user's own key for Claude is taken, and the catalog of Claude is filled with what
        // Claude lists to it (FR-CAT-3). The other providers are as they were.
        await client.ConfirmIdentityAsync();
        Assert.Equal(KeyBrowser.KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);

        var filled = KeyBrowser.Read(await client.PageAsync(CatalogPage));
        var claude = CatalogBrowser.SectionOf(filled, "Claude");

        Assert.Contains("Last synced 15 November 2026, 09:20 UTC.", claude);
        Assert.DoesNotContain("is listed yet", claude);
        Assert.Equal(
            ["claude-test-model", "claude-test-model-mini"], CatalogBrowser.ModelsOf(claude).Select(model => model[1]));
        Assert.All(
            new[] { "Gemini", "GPT", "Grok" }, provider => Assert.Contains(NotSynced, CatalogBrowser.SectionOf(filled, provider)));

        // The key itself is nowhere on the screen, nor its end: the catalog names no key.
        Assert.DoesNotContain(Keys.Claude, filled);
        Assert.DoesNotContain(Keys.Claude[^4..], filled);
    }

    [Fact]
    public async Task A_model_that_its_provider_no_longer_lists_stays_on_the_screen_as_unavailable()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku);
        using var client = await SignedInAsync(application);

        // The provider stops listing one of its models, and three syncs in a row lack it.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus);
        foreach (var minutes in new[] { 10, 20, 30 })
        {
            clock.Set(Morning.AddMinutes(minutes));
            await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);
        }

        var claude = CatalogBrowser.SectionOf(KeyBrowser.Read(await client.PageAsync(CatalogPage)), "Claude");

        // The model is still there, with what was known about it and when it was last listed.
        // It cannot be given to an agent any more. The other model is as it was.
        Assert.Contains("Last synced 15 November 2026, 09:50 UTC.", claude);
        Assert.Equal(
            [
                [
                    "Claude Haiku 4.5", "claude-haiku-4-5", "Unavailable. Last listed on 15 November 2026.", "No", "No",
                    "200,000 tokens", "64,000 tokens", "15 October 2025", "1 August 2026", "1 February 2027",
                    "Experience of Claude Haiku 4.5",
                ],
                [
                    "Claude Opus 5.5", "claude-opus-5-5", "Active", "Yes", "No", "1,000,000 tokens", "64,000 tokens",
                    "21 September 2026", "Unknown", "Unknown", "Experience of Claude Opus 5.5",
                ],
            ],
            CatalogBrowser.ModelsOf(claude));

        // And so is the page that its link leads to: what is recorded about a model still finds it.
        var haiku = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");
        var record = KeyBrowser.Read(await client.PageAsync(WebUiModule.ModelExperiencePath(haiku.Id)));

        Assert.Contains("<h1>Experience of Claude Haiku 4.5</h1>", record);
        Assert.Contains("Status in the catalog: Unavailable. Last listed on 15 November 2026.", record);
    }

    [Fact]
    public async Task A_sync_that_failed_is_said_and_the_time_of_the_last_one_that_succeeded_stays()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(
            clock: clock, settings: file => file.WithCatalogKey().With("AiProviders:Gemini:CatalogKey", GeminiKey));

        // As the application starts, one provider lists its models, and the other gives no list.
        application.ModelLists.Answer = (provider, _) => provider == "claude"
            ? StandInModelLists.Listed(Opus)
            : StandInModelLists.Ended(AiModelListOutcome.Failed);
        using var client = await SignedInAsync(application);

        var atStart = KeyBrowser.Read(await client.PageAsync(CatalogPage));
        var models = CatalogBrowser.ModelsOf(CatalogBrowser.SectionOf(atStart, "Claude"));

        Assert.Contains("Last synced 15 November 2026, 09:20 UTC.", CatalogBrowser.SectionOf(atStart, "Claude"));
        Assert.DoesNotContain("failed", CatalogBrowser.SectionOf(atStart, "Claude"));

        // A provider that has only ever failed was never synced, and the screen says both.
        var gemini = CatalogBrowser.SectionOf(atStart, "Gemini");
        Assert.Contains(NotSynced, gemini);
        Assert.Contains("A sync on 15 November 2026, 09:20 UTC failed, so the catalog of Gemini is still empty.", gemini);
        Assert.Contains("No model of Gemini is listed yet.", gemini);

        // Ten minutes on, the first gives no list either. Its catalog is as it was (FR-CAT-7),
        // so the screen keeps the time that the list is of, and says that a later sync failed.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.TimedOut);
        clock.Set(Morning.AddMinutes(10));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        var failed = CatalogBrowser.SectionOf(KeyBrowser.Read(await client.PageAsync(CatalogPage)), "Claude");

        Assert.Contains("Last synced 15 November 2026, 09:20 UTC.", failed);
        Assert.Contains("A later sync, on 15 November 2026, 09:30 UTC, failed. The list is as it was before.", failed);
        Assert.Equal(models, CatalogBrowser.ModelsOf(failed));

        // Why it failed is for the log and for an operator, and not for every user.
        Assert.DoesNotContain("TimedOut", failed);

        // Ten minutes more, both list their models, and nothing is said of a failure any more.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus);
        clock.Set(Morning.AddMinutes(20));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        var again = KeyBrowser.Read(await client.PageAsync(CatalogPage));

        Assert.All(new[] { "Claude", "Gemini" }, provider =>
        {
            var section = CatalogBrowser.SectionOf(again, provider);

            Assert.Contains("Last synced 15 November 2026, 09:40 UTC.", section);
            Assert.DoesNotContain("failed", section);
        });
    }

    [Fact]
    public async Task The_screen_says_which_models_can_be_given_to_an_agent_and_which_can_search_live()
    {
        // The file of this test says of some models of Claude that they can search live. It
        // says so of every model of Grok, and allows none of them.
        using var application = new AccountsApplication(
            clock: new TestClock(Morning),
            settings: file => file
                .WithCatalogKey()
                .With("AiProviders:Grok:CatalogKey", GrokKey)
                .WithJson("AiProviders:Claude:LiveSearch", """[ "claude-opus-*" ]"""));
        application.ModelLists.Answer = (provider, _) => provider == "claude"
            ? StandInModelLists.Listed(Opus, Haiku, StandInModelLists.Model("claude-next-preview"))
            : StandInModelLists.Listed(StandInModelLists.Model("grok-5"));
        using var client = await SignedInAsync(application);

        var page = KeyBrowser.Read(await client.PageAsync(CatalogPage));

        // By identifier: whether it is for agents, and whether it can search live.
        Assert.Equal(
            [
                ("claude-haiku-4-5", "Yes", "No"),
                ("claude-next-preview", "No", "No"),
                ("claude-opus-5-5", "Yes", "Yes"),
            ],
            CatalogBrowser.ModelsOf(CatalogBrowser.SectionOf(page, "Claude")).Select(model => (model[1], model[3], model[4])));

        // What the file says of a model that it does not allow is shown all the same.
        Assert.Equal(
            [("grok-5", "No", "Yes")],
            CatalogBrowser.ModelsOf(CatalogBrowser.SectionOf(page, "Grok")).Select(model => (model[1], model[3], model[4])));
    }

    [Fact]
    public async Task Each_model_links_to_its_experience_record()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning), settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus, StandInModelLists.Model("claude-next"));
        using var client = await SignedInAsync(application);

        var catalog = KeyBrowser.Read(await client.PageAsync(CatalogPage));

        var opus = StoredAiModel.Of(application.Database, "claude", "claude-opus-5-5");
        var next = StoredAiModel.Of(application.Database, "claude", "claude-next");

        // A link for each model, named after the model, to the page of that model alone.
        Assert.Contains($"<a href=\"/experience/models/{opus.Id}\">Experience of Claude Opus 5.5</a>", catalog);
        Assert.Contains($"<a href=\"/experience/models/{next.Id}\">Experience of claude-next</a>", catalog);
        Assert.Equal(2, Regex.Count(catalog, "<a href=\"/experience/models/"));

        // The page there names the model, says that no trade of the user counts for it yet, and
        // leads back. Its track record is in the tests of the experience screens.
        var record = KeyBrowser.Read(await client.PageAsync(WebUiModule.ModelExperiencePath(opus.Id)));

        Assert.Contains("<h1>Experience of Claude Opus 5.5</h1>", record);
        Assert.Contains("A model of Claude, which names it <code>claude-opus-5-5</code>.", record);
        Assert.Contains("Status in the catalog: Active", record);
        Assert.Contains("Nothing is recorded here about how this model has traded for you in Demo.", record);
        Assert.Contains("<a href=\"/catalog\">Back to the model catalog</a>", record);
        Assert.DoesNotContain("claude-next", record);

        // A model that its provider gives no name is called by its identifier.
        Assert.Contains(
            "<h1>Experience of claude-next</h1>",
            KeyBrowser.Read(await client.PageAsync(WebUiModule.ModelExperiencePath(next.Id))));
    }

    [Theory]
    [InlineData("/experience/models/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37")]
    [InlineData("/experience/models/claude-opus-5-5")]
    [InlineData("/experience/models/")]
    [InlineData("/experience/models")]
    public async Task A_path_that_names_no_model_of_the_catalog_is_not_found(string page)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning), settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus);
        using var client = await SignedInAsync(application);

        using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("Experience of", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/catalog")]
    [InlineData("/experience/models/0192f3a0-7b1c-7c44-9f0e-2a6d5b8c1e37")]
    public async Task A_visitor_who_opens_its_pages_is_sent_to_sign_in(string page)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    [Fact]
    public async Task The_bar_at_the_top_leads_a_signed_in_user_to_the_catalog()
    {
        // The layout marks its elements for its own styles, so the link carries more than its address.
        const string link = "<a href=\"/catalog\"[^>]*>Model catalog</a>";

        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        // A visitor could not open it, and is not shown the way.
        Assert.DoesNotMatch(link, await client.PageAsync("/"));

        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Matches(link, await client.PageAsync("/"));
        Assert.Matches(link, await client.PageAsync("/account"));
    }

    [Fact]
    public async Task What_a_provider_wrote_is_shown_as_text_and_never_as_markup()
    {
        const string name = "<img/src=x/onerror=alert(1)>";
        const string identifier = "claude-<b>bold</b>";

        using var application = new AccountsApplication(clock: new TestClock(Morning), settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model(identifier, name));
        using var client = await SignedInAsync(application);

        var model = StoredAiModel.Of(application.Database, "claude", identifier);

        // The screen, and the page that the link of the model leads to, as the browser gets them.
        string[] pages =
        [
            await client.PageAsync(CatalogPage),
            await client.PageAsync(WebUiModule.ModelExperiencePath(model.Id)),
        ];

        Assert.All(pages, page =>
        {
            Assert.DoesNotContain("<img/src", page);
            Assert.DoesNotContain("<b>bold", page);
            Assert.Contains("&lt;img/src=x/onerror=alert(1)&gt;", page);
            Assert.Contains("claude-&lt;b&gt;bold&lt;/b&gt;", page);
        });
    }

    /// <summary>
    /// A browser of a user who is signed in, once the application has started and the job that
    /// syncs the catalog has had its first run, which is due as the background work starts on a
    /// new database. With a catalog key in the file, the catalog then holds what the providers
    /// list. So the syncs that a test asks for afterwards are the only ones.
    /// </summary>
    private static async Task<HttpClient> SignedInAsync(AccountsApplication application)
    {
        var client = application.CreateClient();

        await Wait.UntilAsync(
            () => StoredJob.Of(application.Database, CatalogApplication.Job)?.Outcome is not null,
            "the first run of the job that syncs the catalog");

        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        return client;
    }
}

/// <summary>What a reader finds on the catalog screen: the providers, and the table of each.</summary>
internal static partial class CatalogBrowser
{
    /// <summary>The names of the providers on the screen, in its order.</summary>
    public static IReadOnlyList<string> ProvidersOn(string page) =>
        [.. Heading().Matches(page).Select(heading => TextOf(heading.Groups[1].Value))];

    /// <summary>The part of the screen that is about a provider, which must be there once.</summary>
    public static string SectionOf(string page, string provider)
    {
        var sections = Section().Matches(page)
            .Select(section => section.Value)
            .Where(section => ProvidersOn(section).SequenceEqual([provider]))
            .ToList();

        Assert.True(sections.Count == 1, $"The screen has {sections.Count} parts about {provider}:{Environment.NewLine}{page}");

        return sections[0];
    }

    /// <summary>The headings of the columns of a provider's table.</summary>
    public static IReadOnlyList<string> ColumnsOf(string section) => RowsOf(section)[0];

    /// <summary>The models of a provider's table, in its order, each as the texts of its cells.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ModelsOf(string section) => [.. RowsOf(section).Skip(1)];

    private static List<IReadOnlyList<string>> RowsOf(string section) =>
    [
        .. Row().Matches(section).Select(row =>
            (IReadOnlyList<string>)[.. Cell().Matches(row.Groups[1].Value).Select(cell => TextOf(cell.Groups[1].Value))]),
    ];

    /// <summary>What a piece of a page says, without its markup.</summary>
    private static string TextOf(string markup) =>
        Space().Replace(Tag().Replace(Comment().Replace(markup, string.Empty), string.Empty), " ").Trim();

    [GeneratedRegex("<section class=\"catalog-provider\">.*?</section>", RegexOptions.Singleline)]
    private static partial Regex Section();

    [GeneratedRegex("<h2[^>]*>(.*?)</h2>", RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex("<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex Row();

    [GeneratedRegex("<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)]
    private static partial Regex Cell();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
