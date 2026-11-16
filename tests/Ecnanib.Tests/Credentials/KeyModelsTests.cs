using System.Reflection;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Catalog;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// When a user's AI key is validated, the system records which catalog models that key can use,
/// and the model selector offers only those (FR-CAT-9). Only eligible models are among them
/// (FR-CAT-6). The selector is not built yet: these tests ask what it will ask.
/// </summary>
/// <remarks>
/// The file of the tests holds no catalog key, so the catalog of a provider is filled with what
/// the provider lists to the keys of users. It allows the models of Claude that <c>claude-*</c>
/// names, but for those that <c>*-preview</c> names, and no model of another provider.
/// </remarks>
public class KeyModelsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;

    // A key of a user for GPT, which the rules accept.
    private const string GptKey = "gpt-0123456789-abcdefghij-klmnop-Hn4V";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 15, 9, 20, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");

    [Fact]
    public async Task The_models_that_a_key_can_use_are_recorded_when_it_is_saved_and_whenever_it_is_validated_again()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The provider takes the key, and lists two models to it.
        application.ModelLists.Answer = (_, _) => Listed("claude-a", "claude-b");

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.Equal(["claude-a", "claude-b"], StoredAiCredential.Of(application.Database, user.Id, "claude").AccessibleModels);

        // A day later the daily check finds that the provider lists others to it: the
        // identifiers are kept as the provider wrote them, in its order.
        application.ModelLists.Answer = (_, _) => Listed("claude-c", "claude-a");
        LastCheckedADayAgo(application);
        await application.CheckKeysAsync(Morning);

        Assert.Equal(["claude-c", "claude-a"], StoredAiCredential.Of(application.Database, user.Id, "claude").AccessibleModels);

        // And so does the check that the user asks for with the button of the list.
        application.ModelLists.Answer = (_, _) => Listed("claude-c");
        clock.Set(Morning.AddMinutes(5));
        await client.CheckAiKeyAsync("claude");

        Assert.Equal(["claude-c"], StoredAiCredential.Of(application.Database, user.Id, "claude").AccessibleModels);

        // The provider was asked for its models with the user's key each time, and with no other.
        Assert.Equal(Enumerable.Repeat(new AskedList("claude", Keys.Claude), 3), application.ModelLists.Asked);
    }

    [Fact]
    public async Task A_key_that_takes_the_place_of_another_keeps_nothing_of_what_that_one_could_use()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        application.ModelLists.Answer = (_, _) => Listed("claude-a", "claude-b");
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        var replaced = StoredAiCredential.Of(application.Database, user.Id, "claude");

        // The provider takes the key that replaces it, and then gives no list.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.Failed);

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Path);

        // The row is the same row, with the new key, and it is valid. What the key before it
        // could use is gone with that key: it is not known what this one can use.
        var stored = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal((replaced.Id, "Qr5T", "Valid"), (stored.Id, stored.MaskedSuffix, stored.Status));
        Assert.Null(stored.AccessibleModels);

        // So nothing of the provider is offered, and the answer says why.
        var offered = await application.KeyModels.SelectableAsync(user.Id, TestContext.Current.CancellationToken);
        Assert.Equal((ModelBlock.NotKnown, 0), (Of(offered, Claude).Block, Of(offered, Claude).Models.Count));

        // The check that the user asks for asks the provider again, which now lists.
        application.ModelLists.Answer = (_, _) => Listed("claude-b");
        clock.Set(Morning.AddMinutes(5));
        await client.CheckAiKeyAsync("claude");

        Assert.Equal(["claude-b"], StoredAiCredential.Of(application.Database, user.Id, "claude").AccessibleModels);
        offered = await application.KeyModels.SelectableAsync(user.Id, TestContext.Current.CancellationToken);
        Assert.Null(Of(offered, Claude).Block);
        Assert.Equal(["claude-b"], Of(offered, Claude).Models.Select(model => model.Identifier));
    }

    [Theory]
    [InlineData(AiModelListOutcome.Failed)]
    [InlineData(AiModelListOutcome.TimedOut)]
    [InlineData(AiModelListOutcome.AuthenticationFailed)]
    public async Task A_provider_that_gives_no_list_leaves_what_was_recorded(AiModelListOutcome outcome)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        application.ModelLists.Answer = (_, _) => Listed("claude-a", "claude-b");
        await client.StoreAiKeyAsync("claude", Keys.Claude);

        // A day later the provider takes the key again, and gives no list.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Ended(outcome);
        LastCheckedADayAgo(application);
        await application.CheckKeysAsync(Morning);

        // The key was checked, and is valid. It is the same key, so what it could use a day ago
        // is still the best that is known of it.
        var key = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal(("Valid", Morning), (key.Status, key.ValidatedAt));
        Assert.Equal(["claude-a", "claude-b"], key.AccessibleModels);
        Assert.Equal(2, application.ModelLists.Asked.Count);

        // That is the catalog's to say, and nothing that Credentials logs.
        Assert.DoesNotContain(
            application.Events(),
            written => written.Area?.StartsWith("Ecnanib.Credentials", StringComparison.Ordinal) == true && written.Level is "Warning" or "Error");
    }

    [Fact]
    public async Task A_list_that_arrives_after_its_key_was_replaced_is_not_recorded()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // A key that was last checked a day ago, which the daily check takes.
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));
        var checkedKey = Keys.Of(user.Id.ToString("N"), "claude");

        // While the provider lists its models to that key, the user stores another in its
        // place, which the provider lists other models to.
        string? replacedOn = null;
        application.ModelLists.AnswerInTime = async (_, key, _) =>
        {
            if (key != checkedKey)
            {
                return Listed("claude-of-the-new-key");
            }

            replacedOn = (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path;

            return Listed("claude-of-the-key-before");
        };

        await application.CheckKeysAsync(Morning);

        Assert.Equal(KeysPage, replacedOn);
        Assert.Equal(
            [new AskedList("claude", checkedKey), new AskedList("claude", Keys.Claude)], application.ModelLists.Asked);

        // The row holds the new key, with what the provider listed to it. What was listed to
        // the key before it came later, and is not written over that.
        var stored = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal("Wx7Z", stored.MaskedSuffix);
        Assert.Equal(["claude-of-the-new-key"], stored.AccessibleModels);
    }

    [Fact]
    public async Task A_list_that_arrives_after_its_key_was_deleted_is_not_recorded_and_fails_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        using var elsewhere = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await elsewhere.SignInAsync(Ada);

        // The key is stored. While its provider lists its models to it, the user deletes it on
        // another browser.
        string? deletedOn = null;
        application.ModelLists.AnswerInTime = async (_, _, _) =>
        {
            deletedOn = (await elsewhere.DeleteAiKeyAsync("gpt")).Path;

            return Listed("gpt-a");
        };

        // The form that stored it is answered as it is when nothing happens meanwhile.
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gpt", GptKey)).Path);
        Assert.Equal(KeysPage, deletedOn);

        // The key is gone, and the list has nothing to be kept with. That is no error.
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.DoesNotContain(application.Events(), written => written.Level is "Error" or "Fatal");
    }

    [Fact]
    [ProvesOwnershipOf("ai_credentials")]
    public async Task Only_the_models_that_the_users_own_key_can_use_are_offered()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var adas = application.CreateClient();
        using var graces = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The provider lists other models to the key of one user than to that of the other.
        // Among those of the first are one that the file denies and one that it does not allow.
        application.ModelLists.Answer = (_, key) => key == Keys.Of("ada", "claude")
            ? Listed("claude-a", "claude-b", "claude-next-preview", "text-embedding-9")
            : Listed("claude-b", "claude-c");

        await adas.SignInAsync(Ada);
        await adas.ConfirmIdentityAsync();
        await adas.StoreAiKeyAsync("claude", Keys.Of("ada", "claude"));
        await graces.SignInAsync(Grace);
        await graces.ConfirmIdentityAsync();
        await graces.StoreAiKeyAsync("claude", Keys.Of("grace", "claude"));

        // The catalog is the same for both, and holds what either key listed.
        var catalog = StoredAiModel.Of(application.Database, "claude").ToDictionary(model => model.Identifier, model => model.Id);
        Assert.Equal(
            ["claude-a", "claude-b", "claude-c", "claude-next-preview", "text-embedding-9"], catalog.Keys.Order(StringComparer.Ordinal));

        // Each is offered the models that their own key can use, and of those the ones that
        // can be selected at all (FR-CAT-6).
        var forAda = await application.KeyModels.SelectableAsync(ada.Id, cancellationToken);
        var forGrace = await application.KeyModels.SelectableAsync(grace.Id, cancellationToken);

        Assert.Equal(["claude-a", "claude-b"], Of(forAda, Claude).Models.Select(model => model.Identifier));
        Assert.Equal(["claude-b", "claude-c"], Of(forGrace, Claude).Models.Select(model => model.Identifier));
        Assert.Equal(catalog["claude-b"], Of(forAda, Claude).Models[1].Id);

        // Every provider has its place in the answer, in the order of their codes. A user has
        // no key for the others, and is offered nothing of them.
        Assert.Equal(
            [("claude", null), ("gemini", ModelBlock.NoKey), ("gpt", ModelBlock.NoKey), ("grok", ModelBlock.NoKey)],
            forAda.Select(provider => (provider.Provider.Value, provider.Block)));

        // A choice is held to the same when it is saved: the model of the other user's key is
        // not one that this user can select, whatever was posted.
        Assert.Null(await application.KeyModels.BlockForAsync(ada.Id, catalog["claude-a"], cancellationToken));
        Assert.Equal(ModelBlock.NotListed, await application.KeyModels.BlockForAsync(grace.Id, catalog["claude-a"], cancellationToken));
        Assert.Equal(ModelBlock.NotListed, await application.KeyModels.BlockForAsync(ada.Id, catalog["claude-c"], cancellationToken));
        Assert.Null(await application.KeyModels.BlockForAsync(grace.Id, catalog["claude-c"], cancellationToken));

        // A model that the key can use, and that the file keeps from being selected, is no
        // choice either.
        Assert.Equal(
            ModelBlock.NotSelectable, await application.KeyModels.BlockForAsync(ada.Id, catalog["claude-next-preview"], cancellationToken));
        Assert.Equal(
            ModelBlock.NotSelectable, await application.KeyModels.BlockForAsync(ada.Id, catalog["text-embedding-9"], cancellationToken));

        // What one user does with their key changes nothing for the other.
        await graces.DeleteAiKeyAsync("claude");

        Assert.Equal(ModelBlock.NoKey, Of(await application.KeyModels.SelectableAsync(grace.Id, cancellationToken), Claude).Block);
        Assert.Equal(
            ["claude-a", "claude-b"],
            Of(await application.KeyModels.SelectableAsync(ada.Id, cancellationToken), Claude).Models.Select(model => model.Identifier));
    }

    [Fact]
    public async Task Nothing_is_offered_for_a_provider_without_a_valid_key_or_before_it_is_known_what_the_key_can_use()
    {
        // The service has a key of its own for Claude here, which lists three models: two that
        // can be selected, and one that the file denies.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, key) => key == SettingsJson.CatalogKey
            ? Listed("claude-test-model", "claude-test-model-mini", "claude-next-preview")
            : StandInModelLists.Ended(AiModelListOutcome.Failed);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 1, "the sync as the application first starts");
        var cancellationToken = TestContext.Current.CancellationToken;
        var catalog = StoredAiModel.Of(application.Database, "claude").ToDictionary(model => model.Identifier, model => model.Id);

        // Without a key for the provider, none of its models is offered.
        Assert.Equal(ModelBlock.NoKey, Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude).Block);
        Assert.Equal(ModelBlock.NoKey, await application.KeyModels.BlockForAsync(user.Id, catalog["claude-test-model"], cancellationToken));

        // With a key that the provider took and has listed nothing to yet, none is either: it
        // is not known what the key can use.
        await client.StoreAiKeyAsync("claude", Keys.Claude);

        Assert.Equal(ModelBlock.NotKnown, Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude).Block);
        Assert.Equal(ModelBlock.NotKnown, await application.KeyModels.BlockForAsync(user.Id, catalog["claude-test-model"], cancellationToken));

        // The next check of the key asks again. The provider lists one of the models to it.
        application.ModelLists.Answer = (_, _) => Listed("claude-test-model");
        clock.Set(Morning.AddMinutes(5));
        await client.CheckAiKeyAsync("claude");

        var offered = Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude);
        Assert.Null(offered.Block);
        Assert.Equal([catalog["claude-test-model"]], offered.Models.Select(model => model.Id));
        Assert.Null(await application.KeyModels.BlockForAsync(user.Id, catalog["claude-test-model"], cancellationToken));
        Assert.Equal(
            ModelBlock.NotListed, await application.KeyModels.BlockForAsync(user.Id, catalog["claude-test-model-mini"], cancellationToken));

        // A model that nobody can select is said to be that, before anything about the key, and
        // so is an id that the catalog does not hold.
        Assert.Equal(
            ModelBlock.NotSelectable, await application.KeyModels.BlockForAsync(user.Id, catalog["claude-next-preview"], cancellationToken));
        Assert.Equal(ModelBlock.NotSelectable, await application.KeyModels.BlockForAsync(user.Id, Guid.CreateVersion7(), cancellationToken));

        // The provider refuses the key at a later check. Nothing can use a key that is not
        // valid, so nothing is offered with it, whatever it could use.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        clock.Set(Morning.AddMinutes(10));
        await client.CheckAiKeyAsync("claude");

        var refused = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal("Invalid", refused.Status);
        Assert.Equal(["claude-test-model"], refused.AccessibleModels);
        Assert.Equal(ModelBlock.KeyNotValid, Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude).Block);
        Assert.Equal(
            ModelBlock.KeyNotValid, await application.KeyModels.BlockForAsync(user.Id, catalog["claude-test-model"], cancellationToken));
    }

    [Fact]
    public async Task A_model_that_the_catalog_comes_to_hold_later_is_offered_without_the_key_being_checked_again()
    {
        // The service has a key of its own for Claude, which is listed one model at first. The
        // key of a user is listed that one and another.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, key) => key == SettingsJson.CatalogKey ? Listed("claude-a") : Listed("claude-a", "claude-b");
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 1, "the sync as the application first starts");
        var cancellationToken = TestContext.Current.CancellationToken;

        await client.StoreAiKeyAsync("claude", Keys.Claude);

        // What the provider listed to the key is kept whole. Only the model that the catalog
        // holds is one of the catalog's, and is offered.
        Assert.Equal(["claude-a", "claude-b"], StoredAiCredential.Of(application.Database, user.Id, "claude").AccessibleModels);
        Assert.Equal(
            ["claude-a"],
            Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude).Models.Select(model => model.Identifier));

        // The next sync of the catalog, with the key of the service, finds the other model.
        application.ModelLists.Answer = (_, _) => Listed("claude-a", "claude-b");
        clock.Set(Morning.AddMinutes(10));
        await application.Catalog.SyncAsync(cancellationToken);

        // It is offered from then on. The user's key was asked for its models once, when it was saved.
        Assert.Equal(
            ["claude-a", "claude-b"],
            Of(await application.KeyModels.SelectableAsync(user.Id, cancellationToken), Claude).Models.Select(model => model.Identifier));
        Assert.Single(application.ModelLists.Asked, asked => asked.Key == Keys.Claude);
    }

    [Fact]
    public void Once_an_agent_is_given_a_model_the_component_that_keeps_agents_asks_which_models_can_be_selected()
    {
        using var model = ModelOnly.Context();

        // Agents keeps the settings of agents, and no model is selected for one: no row of
        // Agents points at a model. As soon as one does, Agents must ask: neither the Catalog
        // nor Credentials can keep an agent from being given a model that cannot be selected,
        // or that its user's key cannot use.
        var modelChoices = AgentsRules.ModelChoices(model.Model);
        var asks = typeof(IKeyModels).Assembly.GetTypes()
            .Where(AgentsRules.IsOfAgents)
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(IKeyModels));

        Assert.True(
            asks || modelChoices.Count == 0,
            $"An agent is given a model now ({string.Join(", ", modelChoices)}), and nothing of Agents asks which models a " +
            "user can select. Only an eligible model can be selected for an agent (FR-CAT-6), and of those only one that the " +
            $"user's key can use (FR-CAT-9). Take {nameof(IKeyModels)} in Ecnanib.Agents, offer what " +
            $"{nameof(IKeyModels.SelectableAsync)} answers, and ask {nameof(IKeyModels.BlockForAsync)} when a choice is saved. " +
            "See docs/credentials.md.");
    }

    /// <summary>A list of models with these identifiers, about which the provider says nothing more.</summary>
    private static AiModelList Listed(params string[] identifiers) =>
        StandInModelLists.Listed([.. identifiers.Select(identifier => StandInModelLists.Model(identifier))]);

    /// <summary>What a user is offered of one provider's models.</summary>
    private static ProviderModels Of(IReadOnlyList<ProviderModels> offered, AiProviderCode provider) =>
        offered.Single(models => models.Provider == provider);

    /// <summary>
    /// Makes every stored key one that its provider last answered a check of a day ago, so that
    /// the daily check takes it now, with the clock where it is.
    /// </summary>
    private static void LastCheckedADayAgo(AccountsApplication application) =>
        application.Database.Execute("UPDATE ai_credentials SET validated_at = validated_at - interval '1 day'");
}
