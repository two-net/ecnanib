using System.Diagnostics;
using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// A sync authenticates with the catalog key that the configuration file holds for the provider.
/// When the file holds none for a provider, that provider's catalog is refreshed whenever a
/// user's key for it is validated (FR-CAT-3). The user's key is handed on for the one list and is
/// kept nowhere, and what the list gives says nothing about the key. Whichever key the catalog
/// is synced with, a user's key is asked for the list of its provider's models when the
/// provider took it, and the catalog answers with what the provider listed to it (FR-CAT-9).
/// </summary>
public class CatalogKeyTests
{
    private const string Ada = "ada@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;

    // A key of a user for GPT, which the rules accept.
    private const string GptKey = "gpt-0123456789-abcdefghij-klmnop-Hn4V";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 13, 9, 20, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");
    private static readonly AiProviderCode Gemini = new("gemini");

    [Fact]
    public async Task Without_a_catalog_key_the_catalog_of_a_provider_is_refreshed_when_a_users_key_for_it_is_saved()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // Nothing is known of any provider before a key is saved.
        Assert.Empty(StoredAiModel.All(application.Database));

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gpt", GptKey)).Path);

        // The provider took the key, and was then asked for its models with it, once.
        Assert.Equal([new AskedKey("gpt", GptKey)], application.Providers.Asked);
        Assert.Equal([new AskedList("gpt", GptKey)], application.ModelLists.Asked);

        // The catalog holds what it listed, and the sync says what it was for.
        Assert.Equal(["gpt-test-model", "gpt-test-model-mini"], StoredAiModel.All(application.Database).Select(model => model.Identifier));
        Assert.Equal(
            [new StoredCatalogSync("gpt", "KeyValidated", Morning, Morning, "Succeeded", null, Added: 2, Changed: 0, Unavailable: 0)],
            StoredCatalogSync.All(application.Database));

        // The key is stored as it would be without the catalog.
        Assert.Equal("Valid", StoredAiCredential.Of(application.Database, user.Id, "gpt").Status);
    }

    [Fact]
    public async Task It_is_refreshed_again_whenever_the_key_is_validated_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        var key = Keys.Of(user.Id.ToString("N"), "claude");

        // A key that was last checked a day ago, which the daily check takes.
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));
        await application.CheckKeysAsync(Morning);

        Assert.Equal([new AskedList("claude", key)], application.ModelLists.Asked);

        // And the check that the user asks for with the button of the list, some minutes later.
        clock.Set(Morning.AddMinutes(5));
        await client.SignInAsync(Ada);
        await client.CheckAiKeyAsync("claude");

        Assert.Equal([new AskedList("claude", key), new AskedList("claude", key)], application.ModelLists.Asked);
        Assert.Equal(
            [("KeyValidated", Morning), ("KeyValidated", Morning.AddMinutes(5))],
            StoredCatalogSync.Of(application.Database, "claude").Select(sync => (sync.Cause, sync.StartedAt)));
        Assert.Equal(2, StoredAiModel.Of(application.Database, "claude").Count);
    }

    [Theory]
    [InlineData(KeyVerdict.Refused)]
    [InlineData(KeyVerdict.Unreachable)]
    public async Task A_key_that_its_provider_did_not_take_refreshes_nothing(KeyVerdict verdict)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.AiAnswer = (_, _) => verdict;

        // Neither a key that is saved, nor one that is stored and checked again.
        await client.StoreAiKeyAsync("gpt", GptKey);
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));
        await application.CheckKeysAsync(Morning);

        Assert.Equal(2, application.Providers.Asked.Count(asked => asked.About is "gpt" or "claude"));
        Assert.Empty(application.ModelLists.Asked);
        Assert.Empty(StoredCatalogSync.All(application.Database));
        Assert.Empty(StoredAiModel.All(application.Database));
    }

    [Fact]
    public async Task With_a_catalog_key_for_a_provider_a_users_key_lists_its_models_and_syncs_nothing()
    {
        // The service has a key of its own for Claude, and none for GPT. A user's key for Claude
        // is listed a model that the key of the service is not.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (provider, key) => key == Keys.Claude
            ? StandInModelLists.Listed(StandInModelLists.Model("claude-test-model"), StandInModelLists.Model("claude-of-this-key-alone"))
            : StandInModelLists.Listed(StandInModelLists.Model($"{provider}-test-model"), StandInModelLists.Model($"{provider}-test-model-mini"));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 1, "the sync as the application first starts");
        var synced = StoredAiModel.Of(application.Database, "claude");

        // Some minutes on, so that a sync with the user's key would show in the catalog.
        clock.Set(Morning.AddMinutes(5));
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gpt", GptKey)).Path);

        // Both providers were asked for their models with the user's key: that is how it is
        // known what the key can use (FR-CAT-9).
        Assert.Equal(
            [new AskedList("claude", SettingsJson.CatalogKey), new AskedList("claude", Keys.Claude), new AskedList("gpt", GptKey)],
            application.ModelLists.Asked);

        // Only the catalog of GPT was synced with it. That of Claude is the key of the file's
        // to fill: it is as that key left it, to the time each model was last seen, and no sync
        // is recorded for the list that the user's key was given.
        Assert.Equal(
            [("claude", "Scheduled"), ("gpt", "KeyValidated")],
            StoredCatalogSync.All(application.Database).Select(sync => (sync.Provider, sync.Cause)));
        Assert.Equal(synced, StoredAiModel.Of(application.Database, "claude"));
        Assert.Equal(["claude-test-model", "claude-test-model-mini"], synced.Select(model => model.Identifier));
    }

    [Fact]
    public async Task Telling_the_catalog_of_a_key_answers_with_the_models_that_the_provider_listed_to_it()
    {
        // One provider that the service has a key for, and one that it has none for. Each lists
        // to a key what the key may use, which is different for each key.
        var providers = new StandInModelLists
        {
            Answer = (provider, key) => key switch
            {
                SettingsJson.CatalogKey => StandInModelLists.Listed(StandInModelLists.Model("claude-a"), StandInModelLists.Model("claude-b")),
                "the-key-of-a-user-who-sees-more" => StandInModelLists.Listed(
                    StandInModelLists.Model($"{provider}-c"), StandInModelLists.Model($"{provider}-a")),
                _ => StandInModelLists.Listed(StandInModelLists.Model($"{provider}-a")),
            },
        };
        using var application = await new CatalogApplication(
            new TestClock(Morning), providers: providers, settings: file => file.WithCatalogKey()).StartedAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        // The identifiers, as the provider wrote them and in its order, with one that the
        // catalog does not hold among them: the catalog of Claude is not this key's to fill.
        Assert.Equal(["claude-c", "claude-a"], await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user-who-sees-more", cancellationToken));
        Assert.Equal(["claude-a"], await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-another-user", cancellationToken));
        Assert.Equal(["claude-a", "claude-b"], StoredAiModel.Of(application.Database, "claude").Select(model => model.Identifier));

        // Where the catalog is synced with the keys of users, the answer is the same, and the
        // catalog then holds what was listed.
        Assert.Equal(["gemini-c", "gemini-a"], await application.Catalog.KeyValidatedAsync(Gemini, "the-key-of-a-user-who-sees-more", cancellationToken));
        Assert.Equal(["gemini-a"], await application.Catalog.KeyValidatedAsync(Gemini, "the-key-of-another-user", cancellationToken));
        Assert.Equal(["gemini-a", "gemini-c"], StoredAiModel.Of(application.Database, "gemini").Select(model => model.Identifier));

        // No answer names a user, and none is kept by the catalog: whose key it was is not told.
        Assert.Equal(
            [("claude", "Scheduled"), ("gemini", "KeyValidated"), ("gemini", "KeyValidated")],
            StoredCatalogSync.All(application.Database).Select(sync => (sync.Provider, sync.Cause)));
    }

    [Fact]
    public async Task A_provider_that_gives_a_users_key_no_list_is_answered_as_not_known()
    {
        var clock = new TestClock(Morning);
        var providers = new StandInModelLists();
        using var application = await new CatalogApplication(clock, providers: providers, settings: file => file.WithCatalogKey()).StartedAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var synced = StoredAiModel.All(application.Database);

        // With the key of the service for the provider, and without one: no list is no answer,
        // and not a list of nothing.
        foreach (var outcome in new[] { AiModelListOutcome.Failed, AiModelListOutcome.AuthenticationFailed })
        {
            providers.Answer = (_, _) => StandInModelLists.Ended(outcome);

            Assert.Null(await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user", cancellationToken));
            Assert.Null(await application.Catalog.KeyValidatedAsync(Gemini, "the-key-of-a-user", cancellationToken));
        }

        // So is a list that failed outright.
        providers.Answer = (_, _) => throw new InvalidOperationException("The list could not be read.");

        Assert.Null(await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user", cancellationToken));

        // Where the service has a key of its own, the catalog is as it was, and no sync is
        // recorded for a list that was only to say what a key can use. The log says each time
        // that it is not known: twice as a warning, and once as an error of the application.
        Assert.Equal(synced, StoredAiModel.All(application.Database));
        Assert.Equal(["Scheduled"], StoredCatalogSync.Of(application.Database, "claude").Select(sync => sync.Cause));
        Assert.Equal(
            ["Warning", "Warning", "Error"],
            application.CatalogEvents()
                .Where(written => written.Template.Contains("Which models the key can use is not known", StringComparison.Ordinal))
                .Select(written => written.Level));

        // Where it has none, each of them is a sync that failed, as before.
        Assert.Equal(
            ["NoList", "KeyRefused"], StoredCatalogSync.Of(application.Database, "gemini").Select(sync => sync.Failure));

        // A list that ran out of time is not known either, and the provider is then left alone
        // for a minute, whichever key the catalog is synced with: the next key is not listed.
        providers.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.TimedOut);
        Assert.Null(await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user", cancellationToken));

        var asked = providers.Asked.Count;
        providers.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-a"));
        clock.Set(Morning.AddSeconds(59));

        Assert.Null(await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-the-next-user", cancellationToken));
        Assert.Equal(asked, providers.Asked.Count);

        // After the minute it is asked again, and answers.
        clock.Set(Morning.AddSeconds(60));

        Assert.Equal(["claude-a"], await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-the-next-user", cancellationToken));
    }

    [Fact]
    public async Task A_list_that_could_not_be_stored_was_listed_to_the_key_all_the_same()
    {
        // The catalog of Claude is synced with the keys of users here. What depends on a model
        // cannot be told, so a sync that would mark a model fails, and stores nothing.
        const string wide = "the-key-of-a-user-who-sees-both";
        const string narrow = "the-key-of-a-user-who-sees-one";

        var clock = new TestClock(Morning);
        var providers = new StandInModelLists
        {
            Answer = (_, key) => key == wide
                ? StandInModelLists.Listed(StandInModelLists.Model("claude-a"), StandInModelLists.Model("claude-b"))
                : StandInModelLists.Listed(StandInModelLists.Model("claude-a")),
        };
        using var application = await new CatalogApplication(
            clock, providers: providers, services: services => services.AddSingleton<IModelDependents>(new FailingDependents())).StartedAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        await application.Catalog.KeyValidatedAsync(Claude, wide, cancellationToken);

        // The key that sees one is checked daily. The fourth day is the first on which no key
        // has listed the other model for long enough, so that sync would mark it, and fails.
        for (var day = 1; day <= 4; day++)
        {
            clock.Set(Morning.AddDays(day));

            Assert.Equal(["claude-a"], await application.Catalog.KeyValidatedAsync(Claude, narrow, cancellationToken));
        }

        // What the provider listed to the key is what the key can use, whatever became of the
        // catalog: the sync is recorded as one that failed, and the model is as it was.
        Assert.Equal(
            ["Succeeded", "Succeeded", "Succeeded", "Succeeded", "Failed"],
            StoredCatalogSync.Of(application.Database, "claude").Select(sync => sync.Result));
        Assert.Equal("Active", StoredAiModel.Of(application.Database, "claude", "claude-b").Status);
    }

    [Fact]
    public async Task What_goes_wrong_with_the_list_says_nothing_about_the_key()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The provider takes the key, and then gives no list.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.Failed);

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gpt", GptKey)).Path);
        Assert.Equal("Valid", StoredAiCredential.Of(application.Database, user.Id, "gpt").Status);

        // Or what stores the list fails outright, a day later, when the key is checked again.
        application.ModelLists.Answer = (_, _) => throw new InvalidOperationException("The list could not be read.");
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(Morning.AddDays(1));

        var key = StoredAiCredential.Of(application.Database, user.Id, "gpt");
        Assert.Equal(("Valid", Morning.AddDays(1)), (key.Status, key.ValidatedAt));

        // Both are the catalog's own to record and to say, and neither is in what Credentials logs.
        Assert.Equal(
            [("KeyValidated", "Failed", "NoList"), ("KeyValidated", "Failed", "Error")],
            StoredCatalogSync.Of(application.Database, "gpt").Select(sync => (sync.Cause, sync.Result, sync.Failure)));
        Assert.Equal(
            ["Warning", "Error"],
            application.Events().Where(written => written.Area == CatalogApplication.Area).Select(written => written.Level));
        Assert.DoesNotContain(
            application.Events(),
            written => written.Area?.StartsWith("Ecnanib.Credentials", StringComparison.Ordinal) == true && written.Level is "Warning" or "Error");
    }

    [Fact]
    public async Task A_provider_whose_list_ran_out_of_time_with_a_users_key_is_left_alone_for_a_minute()
    {
        var clock = new TestClock(Morning);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.TimedOut) };
        using var application = await new CatalogApplication(clock, providers: providers).StartedAsync();

        // The keys of many users are checked one after the other. The provider is waited for once.
        foreach (var seconds in new[] { 0, 1, 30, 59 })
        {
            clock.Set(Morning.AddSeconds(seconds));
            await application.Catalog.KeyValidatedAsync(Claude, $"the-key-of-user-{seconds}", TestContext.Current.CancellationToken);
        }

        Assert.Equal([new AskedList("claude", "the-key-of-user-0")], providers.Asked);
        Assert.Single(StoredCatalogSync.All(application.Database));

        // Another provider is asked all the same.
        await application.Catalog.KeyValidatedAsync(Gemini, "a-key-for-gemini", TestContext.Current.CancellationToken);

        Assert.Equal(new AskedList("gemini", "a-key-for-gemini"), providers.Asked[^1]);

        // A minute after it ran out of time, the provider is asked again, and when it lists its
        // models, every key that follows is used.
        providers.Answer = (provider, _) => provider == "claude"
            ? StandInModelLists.Listed(StandInModelLists.Model("claude-a"))
            : StandInModelLists.Ended(AiModelListOutcome.TimedOut);

        foreach (var seconds in new[] { 60, 61, 62 })
        {
            clock.Set(Morning.AddSeconds(seconds));
            await application.Catalog.KeyValidatedAsync(Claude, $"the-key-of-user-{seconds}", TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            ["the-key-of-user-0", "the-key-of-user-60", "the-key-of-user-61", "the-key-of-user-62"],
            providers.Asked.Where(asked => asked.Provider == "claude").Select(asked => asked.Key));
    }

    [Theory]
    [InlineData(AiModelListOutcome.Failed)]
    [InlineData(AiModelListOutcome.AuthenticationFailed)]
    public async Task A_list_that_failed_at_once_keeps_no_other_key_from_listing(AiModelListOutcome outcome)
    {
        // One user's key gets no list: it lists no model, or the provider refuses it the list.
        // That may be about this key alone, and it took no time.
        const string without = "the-key-of-a-user-who-gets-no-list";

        var clock = new TestClock(Morning);
        var providers = new StandInModelLists
        {
            Answer = (_, key) => key == without ? StandInModelLists.Ended(outcome) : StandInModelLists.Listed(StandInModelLists.Model("claude-a")),
        };
        using var application = await new CatalogApplication(clock, providers: providers).StartedAsync();

        // The keys that are checked right after it are used, every day in the same order.
        await application.Catalog.KeyValidatedAsync(Claude, without, TestContext.Current.CancellationToken);
        await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-the-next-user", TestContext.Current.CancellationToken);
        await application.Catalog.KeyValidatedAsync(Claude, without, TestContext.Current.CancellationToken);
        await application.Catalog.KeyValidatedAsync(Claude, "the-key-of-the-user-after", TestContext.Current.CancellationToken);

        Assert.Equal(4, providers.Asked.Count);
        Assert.Equal(
            ["Failed", "Succeeded", "Failed", "Succeeded"],
            StoredCatalogSync.Of(application.Database, "claude").Select(sync => sync.Result));
        Assert.Equal("claude-a", Assert.Single(StoredAiModel.All(application.Database)).Identifier);
    }

    [Fact]
    public async Task A_list_that_is_asked_with_a_users_key_is_given_up_after_15_seconds()
    {
        // A provider that never answers, and a clock that shows which limit the list was given.
        var clock = new TimerClock();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = new StandInModelLists
        {
            AnswerInTime = async (_, _, givenUp) =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, givenUp);

                throw new UnreachableException();
            },
        };
        using var application = await new CatalogApplication(clock, providers: providers).StartedAsync();

        var telling = application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user", TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The time that the check of a key may take: a user waits for both with one form.
        var limit = TimeSpan.FromSeconds(15);
        Assert.Contains(limit, clock.Timers);
        Assert.False(telling.IsCompleted);

        // When it runs out, telling ends, without an error, and the sync is recorded as one that timed out.
        clock.RunOut(limit);
        await telling;

        var sync = Assert.Single(StoredCatalogSync.All(application.Database));
        Assert.Equal(("claude", "KeyValidated", "Failed", "TimedOut"), (sync.Provider, sync.Cause, sync.Result, sync.Failure));
    }

    [Fact]
    public async Task A_refresh_that_its_caller_cancels_ends_as_cancelled_and_records_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        var providers = new StandInModelLists
        {
            AnswerInTime = (_, _, _) =>
            {
                cancelled.Cancel();

                throw new OperationCanceledException(cancelled.Token);
            },
        };
        using var application = await new CatalogApplication(new TestClock(Morning), providers: providers).StartedAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => application.Catalog.KeyValidatedAsync(Claude, "the-key-of-a-user", cancelled.Token));

        Assert.Empty(StoredCatalogSync.All(application.Database));
    }

    [Fact]
    public async Task A_users_key_is_in_nothing_that_is_kept_or_logged_and_the_rows_of_the_catalog_name_no_user()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(
            clock: clock, settings: file => file.With("Logging:MinimumLevel", "Debug"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gpt", GptKey)).Path);
        Assert.Equal(new AskedList("gpt", GptKey), Assert.Single(application.ModelLists.Asked));

        // The rows of the catalog are the service's, and the same for every user: they name
        // neither the user whose key listed the models nor the key.
        var kept = application.Database.Column("SELECT row_to_json(stored)::text FROM ai_models stored")
            .Concat(application.Database.Column("SELECT row_to_json(stored)::text FROM catalog_sync_runs stored"))
            .ToList();
        Assert.Equal(3, kept.Count);
        Assert.All(
            new[] { user.Id.ToString("D"), user.Id.ToString("N") }.Concat(Keys.FormsOf(GptKey)),
            form => Assert.All(kept, row => Assert.DoesNotContain(form, row, StringComparison.OrdinalIgnoreCase)));

        // The key is in no table readable, and in no log event, at the level that logs the most.
        Assert.All(Keys.FormsOf(GptKey), form => Assert.DoesNotContain(form, application.EverythingStored()));
        Assert.All(Keys.FormsOf(GptKey), form => Assert.DoesNotContain(form, application.LogText()));
    }

    /// <summary>Stands for what depends on a model, and can never be told: a change to the agents that cannot be stored.</summary>
    private sealed class FailingDependents : IModelDependents
    {
        public Task ModelsUnavailableAsync(
            EcnanibDbContext context, AiProviderCode provider, IReadOnlyList<Guid> modelIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The agents of the test could not be changed.");
    }
}
