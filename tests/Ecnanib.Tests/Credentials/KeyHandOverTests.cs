using Ecnanib.Credentials;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// A stored key is handed to the call of an agent that needs it, decrypted only in memory and
/// only for that call (FR-KEY-9). The key is the one of the agent's own user, and no other
/// (FR-KEY-1). See "Handing a key to a call" in docs/credentials.md.
/// </summary>
public class KeyHandOverTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");

    [Fact]
    public async Task A_call_is_handed_the_key_of_its_own_user_for_its_provider_and_no_other()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        await application.GiveAiKeyAsync(ada.Id, "claude", Morning, accessibleModels: null);
        await application.GiveAiKeyAsync(ada.Id, "grok", Morning, accessibleModels: null);
        await application.GiveAiKeyAsync(grace.Id, "claude", Morning, accessibleModels: null);

        var handed = await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken);

        // The key as its user stored it, which the database holds only encrypted.
        Assert.Equal(KeyHandOverOutcome.Handed, handed.Outcome);
        Assert.Equal(Keys.Of(ada.Id.ToString("N"), "claude"), handed.Key);

        // Each provider has the key that its user gave it, and each user their own.
        Assert.Equal(
            Keys.Of(ada.Id.ToString("N"), "grok"),
            (await application.KeyHandOver.AiKeyAsync(ada.Id, new AiProviderCode("grok"), TestContext.Current.CancellationToken)).Key);
        Assert.Equal(
            Keys.Of(grace.Id.ToString("N"), "claude"),
            (await application.KeyHandOver.AiKeyAsync(grace.Id, Claude, TestContext.Current.CancellationToken)).Key);

        // Nothing is written by handing a key over: neither a row, nor an event that holds it.
        Assert.All(Keys.FormsOf(handed.Key!), form =>
        {
            Assert.DoesNotContain(form, application.EverythingStored(), StringComparison.Ordinal);
            Assert.DoesNotContain(form, application.LogText(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_user_who_has_no_key_for_a_provider_is_handed_none_and_never_the_key_of_another_user()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Grace has a key for the provider, and Ada has none: there is no key to fall back on.
        await application.GiveAiKeyAsync(grace.Id, "claude", Morning, accessibleModels: null);

        var none = await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken);

        Assert.Equal(KeyHandOverOutcome.NoKey, none.Outcome);
        Assert.Null(none.Key);

        // Nor is a user who does not exist handed anything.
        Assert.Equal(
            KeyHandOverOutcome.NoKey,
            (await application.KeyHandOver.AiKeyAsync(Guid.CreateVersion7(), Claude, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_key_that_its_provider_refused_is_handed_to_no_call()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);

        await application.GiveAiKeyAsync(ada.Id, "claude", Morning, accessibleModels: null);
        application.Database.Execute("UPDATE ai_credentials SET status = 'Invalid'");

        var refused = await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken);

        Assert.Equal(KeyHandOverOutcome.Invalid, refused.Outcome);
        Assert.Null(refused.Key);

        // A key that was never checked is one that its provider has not refused.
        application.Database.Execute("UPDATE ai_credentials SET status = 'Unchecked'");

        Assert.Equal(
            KeyHandOverOutcome.Handed,
            (await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_key_that_does_not_decrypt_is_handed_to_no_call_and_the_log_says_so_without_it()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);

        await application.GiveAiKeyAsync(ada.Id, "claude", Morning, accessibleModels: null);

        // The row names a master key that the configuration file does not list.
        application.Database.Execute("UPDATE ai_credentials SET master_key_id = 'retired'");

        var unreadable = await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken);

        Assert.Equal(KeyHandOverOutcome.Unreadable, unreadable.Outcome);
        Assert.Null(unreadable.Key);

        var said = Assert.Single(
            application.Events(),
            written => written.Template == "The key of user {UserId} for the AI provider {Provider} cannot be read, so it is handed to no call");

        Assert.Equal("Error", said.Level);
        Assert.Equal((ada.Id.ToString(), "claude"), (said.Text("UserId"), said.Text("Provider")));

        // A value that was copied to the row of another user does not decrypt there either: it
        // is bound to its row and its owner.
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveAiKeyAsync(grace.Id, "claude", Morning, accessibleModels: null);
        application.Database.Execute(
            $"UPDATE ai_credentials SET master_key_id = (SELECT master_key_id FROM ai_credentials WHERE user_id = '{grace.Id}'), " +
            $"encrypted_value = (SELECT encrypted_value FROM ai_credentials WHERE user_id = '{grace.Id}') WHERE user_id = '{ada.Id}'");

        Assert.Equal(
            KeyHandOverOutcome.Unreadable,
            (await application.KeyHandOver.AiKeyAsync(ada.Id, Claude, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public void What_is_handed_over_writes_its_outcome_and_never_the_key()
    {
        var handed = HandedKey.Of(Keys.Claude);

        // Whatever writes it as text, in a log event or an error, writes no key.
        Assert.Equal("Handed", handed.ToString());
        Assert.Equal("Handed", $"{handed}");
        Assert.Equal("NoKey", HandedKey.None(KeyHandOverOutcome.NoKey).ToString());

        // A key that is handed over is a key, and one that is not has none.
        Assert.Throws<ArgumentOutOfRangeException>(() => HandedKey.None(KeyHandOverOutcome.Handed));
        Assert.Throws<ArgumentException>(() => HandedKey.Of(string.Empty));
    }
}
