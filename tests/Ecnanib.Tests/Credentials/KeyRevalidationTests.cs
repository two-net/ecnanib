using System.Collections.Concurrent;
using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// Keys are validated again daily and whenever a call fails authentication (FR-KEY-3). A key
/// that its provider then refuses is invalid, and what depends on it is told, so that an agent
/// moves to Attention required (FR-KEY-10). A stored Binance key that turns out to be able to
/// withdraw is deleted (FR-KEY-4, NFR-LEG-2). A user can have a stored key checked at once.
/// </summary>
public class KeyRevalidationTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;
    private const string ValidatorArea = "Ecnanib.Credentials.KeyValidator";
    private const string JobArea = "Ecnanib.Credentials.KeyValidationJob";

    // Between two hours, so that the job is not due when the application starts.
    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 20, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");

    [Fact]
    public async Task The_stored_keys_are_checked_by_a_job_that_runs_on_the_hour()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var user = await application.RegisterVerifiedAsync(Ada);

        // Keys that were last checked a day ago, stored as the application stores them.
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));

        // Nothing is checked when the application starts: the job waits for the hour.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(application.Providers.Asked);
        Assert.Null(StoredJob.Of(application.Database, "key-validation"));

        // On the hour it runs, and asks each provider about the key that is due.
        clock.Set(Morning.AddMinutes(45));
        await Wait.UntilAsync(
            () => StoredJob.Of(application.Database, "key-validation")?.Outcome == "Succeeded", "the run of the job on the hour");

        // Each key was read from what is stored, whole: the key of Claude, and both keys of Binance.
        var owner = user.Id.ToString("N");
        Assert.Equal(
            [new AskedKey("claude", Keys.Of(owner, "claude")), new AskedKey("Demo", Keys.Of(owner, "demo"), Keys.Of(owner, "seal"))],
            application.Providers.Asked);

        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        Assert.Equal(("Valid", Morning.AddMinutes(45)), (aiKey.Status, aiKey.ValidatedAt));
        Assert.Equal(("Valid", Morning.AddMinutes(45)), (exchangeKey.Status, exchangeKey.ValidatedAt));

        // A run with nothing to say says nothing: no warning, and no key.
        Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");
        Assert.All(
            new[] { Keys.Of(owner, "claude"), Keys.Of(owner, "demo"), Keys.Of(owner, "seal") }.SelectMany(Keys.FormsOf),
            form => Assert.DoesNotContain(form, application.LogText()));
    }

    [Fact]
    public async Task A_key_is_checked_again_once_a_day_has_passed_since_its_provider_last_answered()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        clock.Set(Morning.AddMinutes(5));
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        Assert.Equal(2, application.Providers.Asked.Count);

        // Not before the day is over: the runs of the hours until then ask nobody.
        foreach (var hours in new[] { 1, 12, 23 })
        {
            clock.Set(Morning.AddHours(hours).AddMinutes(-20));
            await application.CheckKeysAsync(clock.GetUtcNow());
        }

        Assert.Equal(2, application.Providers.Asked.Count);

        // The first run after it asks about the key that is due, and only that one.
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(
            [new AskedKey("claude", Keys.Claude)], application.Providers.Asked.Skip(2));
        Assert.Equal(Morning.AddDays(1), StoredAiCredential.Of(application.Database, user.Id, "claude").ValidatedAt);
        Assert.Equal(Morning.AddMinutes(5), StoredExchangeCredential.Of(application.Database, user.Id, "Live").ValidatedAt);

        // The other one follows when its day is over, and the first is not due again until
        // a day after its own check.
        clock.Set(Morning.AddDays(1).AddMinutes(40));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(new AskedKey("Live", Keys.BinanceApi, Keys.BinanceSecret), Assert.Single(application.Providers.Asked.Skip(3)));
        Assert.Equal(Morning.AddDays(1).AddMinutes(40), StoredExchangeCredential.Of(application.Database, user.Id, "Live").ValidatedAt);
        Assert.Equal(Morning.AddDays(1), StoredAiCredential.Of(application.Database, user.Id, "claude").ValidatedAt);
    }

    [Fact]
    public async Task A_key_that_was_stored_before_keys_were_checked_is_checked_with_the_next_run()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning.AddMinutes(-10));

        // As the migration leaves the rows that were there: never checked.
        application.Database.Execute(
            "UPDATE ai_credentials SET status = 'Unchecked', validated_at = NULL; " +
            "UPDATE exchange_credentials SET status = 'Unchecked', validated_at = NULL");

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        Assert.Equal(2, CountOf(await client.PageAsync(KeysPage), "Not checked yet. It is checked within the hour."));

        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(["claude", "Demo"], application.Providers.Asked.Select(asked => asked.About));
        Assert.Equal(("Valid", Morning), StatusOfAiKey(application, user.Id));
        Assert.Equal(("Valid", Morning), StatusOfExchangeKey(application, user.Id, "Demo"));
    }

    [Fact]
    public async Task A_key_that_its_provider_refuses_becomes_invalid_and_what_depends_on_it_is_told_once()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents();
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var encrypted = StoredAiCredential.Of(application.Database, user.Id, "claude").EncryptedValue;

        // The keys were revoked at the providers. The daily check finds out.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Refused;
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(("Invalid", Morning.AddDays(1)), StatusOfAiKey(application, user.Id));
        Assert.Equal(("Invalid", Morning.AddDays(1)), StatusOfExchangeKey(application, user.Id, "Demo"));

        // What depends on each key was told, inside the transaction, while the key was still
        // marked valid: an agent is not left running on a key that is refused (FR-KEY-10).
        Assert.Equal(
            [
                new Told("invalid", user.Id, "claude", InTransaction: true, StatusThen: "Valid"),
                new Told("invalid", user.Id, "Demo", InTransaction: true, StatusThen: "Valid"),
            ],
            dependents.Told);

        // The key stays stored, as it was: its user replaces it, or puts right what is wrong.
        Assert.Equal(encrypted, StoredAiCredential.Of(application.Database, user.Id, "claude").EncryptedValue);
        await client.SignInAsync(Ada);
        var list = KeyBrowser.Read(await client.PageAsync(KeysPage));
        Assert.Contains("Not valid: Claude refused it on 13 November 2026, 09:20 UTC. Nothing can use it until Claude takes it again.", list);
        Assert.Contains("Not valid: Binance refused it on 13 November 2026, 09:20 UTC.", list);

        Assert.Equal(
            [
                "The key of user {UserId} for the AI provider {Provider} was refused by the provider, and is invalid",
                "The key of user {UserId} of Binance for {TradingMode} was refused by Binance, and is invalid",
            ],
            application.Events().Where(written => written.Area == ValidatorArea).Select(written => written.Template));

        // It is asked about again the next day. It is still refused, and nothing is told
        // again: what depends on it knows.
        clock.Set(Morning.AddDays(2));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(("Invalid", Morning.AddDays(2)), StatusOfAiKey(application, user.Id));
        Assert.Equal(2, dependents.Told.Count);

        // Once the provider takes it again, it is valid again, and can become invalid anew.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Accepted;
        clock.Set(Morning.AddDays(3));
        await application.CheckKeysAsync(clock.GetUtcNow());
        Assert.Equal(("Valid", Morning.AddDays(3)), StatusOfAiKey(application, user.Id));

        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        clock.Set(Morning.AddDays(4));
        await application.CheckKeysAsync(clock.GetUtcNow());
        Assert.Equal(("Invalid", Morning.AddDays(4)), StatusOfAiKey(application, user.Id));
        Assert.Equal(new Told("invalid", user.Id, "claude", InTransaction: true, StatusThen: "Valid"), dependents.Told[^1]);
        Assert.Equal(3, dependents.Told.Count);
    }

    [Fact]
    public async Task A_key_is_not_marked_invalid_when_what_depends_on_it_cannot_be_told_and_the_other_keys_are_still_checked()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents { Fails = true };
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(ada.Id, Morning.AddDays(-2));
        await application.GiveKeysAsync(grace.Id, Morning.AddDays(-1));
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;

        // The run does not fail for it: each key is checked on its own.
        await application.CheckKeysAsync(clock.GetUtcNow());

        // The keys that could not be marked are as they were, and are due again.
        Assert.Equal(("Valid", Morning.AddDays(-2)), StatusOfAiKey(application, ada.Id));
        Assert.Equal(("Valid", Morning.AddDays(-1)), StatusOfAiKey(application, grace.Id));
        Assert.Equal(2, dependents.Told.Count);

        // The keys after them were checked all the same.
        Assert.Equal(("Valid", Morning), StatusOfExchangeKey(application, ada.Id, "Demo"));
        Assert.Equal(("Valid", Morning), StatusOfExchangeKey(application, grace.Id, "Demo"));

        // The log says which keys, by their users, and is an error each time.
        var failed = application.Events().Where(written => written.Area == JobArea).ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, written =>
        {
            Assert.Equal("Error", written.Level);
            Assert.Equal("The daily check of the key of user {UserId} for the AI provider {Provider} failed", written.Template);
            Assert.Contains("The agents of the test could not be changed.", written.Exception);
        });
        Assert.Equal([ada.Id.ToString(), grace.Id.ToString()], failed.Select(written => written.Text("UserId")));
    }

    [Fact]
    [ShowsAuditOf(AuditAction.KeyRejected)]
    public async Task A_stored_Binance_key_that_turns_out_to_be_able_to_withdraw_is_deleted_and_recorded()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents();
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));
        var stored = StoredExchangeCredential.Of(application.Database, user.Id, "Live");

        // Its user turns withdrawals on for the key at Binance, after it was stored.
        application.Providers.BinanceAnswer = (environment, _) =>
            environment == TradingMode.Live ? StandInProviders.TakenInLive with { CanWithdraw = true } : StandInProviders.TakenInDemo;
        clock.Set(Morning.AddDays(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        // The key is gone, for good: Ecnanib holds no key that can withdraw (NFR-LEG-2).
        Assert.Equal(["Demo"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));
        Assert.DoesNotContain(Convert.ToHexString(stored.EncryptedValue), application.EverythingStored(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lm3N", application.EverythingStored());

        // What depends on it was told as when a user deletes it: in the transaction, while the
        // key was still there.
        Assert.Equal([new Told("deleted", user.Id, "Live", InTransaction: true, StatusThen: "Valid")], dependents.Told);

        // The audit trail has it, by the id of the key and for its owner. The service did it:
        // the event has no origin.
        var recorded = StoredAuditEvent.All(application.Database)[^1];
        Assert.Equal(
            new StoredAuditEvent(recorded.Id, user.Id, "KeyRejected", $"exchange-key:{stored.Id}", Morning.AddDays(1), Origin: null),
            recorded);

        var said = Assert.Single(application.Events(), written => written.Area == ValidatorArea);
        Assert.Equal("The key of user {UserId} of Binance for {TradingMode} can withdraw, as Binance reports, and was deleted", said.Template);
        Assert.Equal((user.Id.ToString(), "Live"), (said.Text("UserId"), said.Text("TradingMode")));

        // The list has the place empty again.
        await client.SignInAsync(Ada);
        Assert.Contains("<a href=\"/account/keys/binance/live\">Add a Live key</a>", await client.PageAsync(KeysPage));
    }

    [Fact]
    public async Task A_key_that_can_withdraw_stays_when_what_depends_on_it_cannot_be_told()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents { Fails = true };
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning.AddDays(-1));
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { CanWithdraw = true };

        // An agent is not left running on a key that is gone: the deletion is undone, and is
        // tried again with the next run.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => application.KeyValidation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Demo, TestContext.Current.CancellationToken));

        Assert.Equal(("Valid", Morning.AddDays(-1)), StatusOfExchangeKey(application, user.Id, "Demo"));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "KeyRejected");
    }

    [Fact]
    public async Task A_provider_that_gives_no_answer_leaves_the_keys_as_they_were_and_is_asked_once_in_a_run()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(ada.Id, Morning.AddDays(-2));
        await application.GiveKeysAsync(grace.Id, Morning.AddDays(-1));
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Unreachable;
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Unreachable;

        await application.CheckKeysAsync(clock.GetUtcNow());

        // A provider that is down is asked once, and not once for every user. The key that was
        // checked longest ago comes first.
        var first = ada.Id.ToString("N");
        Assert.Equal(
            [new AskedKey("claude", Keys.Of(first, "claude")), new AskedKey("Demo", Keys.Of(first, "demo"), Keys.Of(first, "seal"))],
            application.Providers.Asked);

        // No answer says nothing about a key: every key is as it was, and still due.
        Assert.Equal(("Valid", Morning.AddDays(-2)), StatusOfAiKey(application, ada.Id));
        Assert.Equal(("Valid", Morning.AddDays(-1)), StatusOfAiKey(application, grace.Id));
        Assert.Equal(("Valid", Morning.AddDays(-2)), StatusOfExchangeKey(application, ada.Id, "Demo"));
        Assert.Equal(
            [
                "The key of user {UserId} for the AI provider {Provider} was not checked: the provider gave no answer. It is checked again later",
                "The key of user {UserId} of Binance for {TradingMode} was not checked: Binance gave no answer. It is checked again later",
            ],
            application.Events().Where(written => written.Area == ValidatorArea).Select(written => written.Template));

        // An hour later the providers answer, and every key that was due is checked.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Accepted;
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo;
        clock.Set(Morning.AddHours(1));
        await application.CheckKeysAsync(clock.GetUtcNow());

        Assert.Equal(6, application.Providers.Asked.Count);
        Assert.All(
            new[] { ada.Id, grace.Id },
            userId =>
            {
                Assert.Equal(("Valid", Morning.AddHours(1)), StatusOfAiKey(application, userId));
                Assert.Equal(("Valid", Morning.AddHours(1)), StatusOfExchangeKey(application, userId, "Demo"));
            });
    }

    [Fact]
    public async Task A_key_that_is_replaced_while_its_provider_is_asked_does_not_get_what_was_said_about_the_one_before_it()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents();
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // The stored keys are no good any more: one was revoked, and the other can withdraw.
        // While the provider is asked about each, its user stores another one in its place.
        application.Providers.AiAnswer = (_, key) => key == Keys.Claude ? KeyVerdict.Refused : KeyVerdict.Accepted;
        application.Providers.BinanceAnswer = (_, key) =>
            key == Keys.BinanceApi ? StandInProviders.TakenInLive with { CanWithdraw = true } : StandInProviders.TakenInLive;
        clock.Set(Morning.AddMinutes(5));

        application.Providers.WhileAsked = () => client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        Assert.Equal(
            KeyCheckOutcome.Changed, await application.KeyValidation.RevalidateAiKeyAsync(user.Id, Claude, TestContext.Current.CancellationToken));

        application.Providers.WhileAsked = () => client.StoreExchangeKeyAsync("live", Keys.Of("another", "live"), Keys.Of("another-sealed", "live"));
        Assert.Equal(
            KeyCheckOutcome.Changed,
            await application.KeyValidation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Live, TestContext.Current.CancellationToken));

        // The keys that are stored now are the new ones, valid as their own checks found
        // them. Nothing was deleted, and nothing was told.
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal(("Qr5T", "Valid", Morning.AddMinutes(5)), (aiKey.MaskedSuffix, aiKey.Status, aiKey.ValidatedAt));

        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Live");
        Assert.Equal(("live", "Valid", Morning.AddMinutes(5)), (exchangeKey.MaskedSuffix, exchangeKey.Status, exchangeKey.ValidatedAt));
        Assert.Empty(dependents.Told);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "KeyRejected");
    }

    [Fact]
    public async Task A_key_is_checked_again_at_once_for_a_call_that_failed_authentication()
    {
        var clock = new TestClock(Morning);
        var dependents = new RecordingDependents();
        using var application = new AccountsApplication(
            clock: clock, host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        var validation = application.KeyValidation;
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning.AddHours(-2));

        // Two hours after the key was checked, a call with it fails authentication: the key
        // was revoked meanwhile. Whatever made the call has the key checked again, now, and
        // does not wait for the day to be over.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;

        Assert.Equal(KeyCheckOutcome.Invalid, await validation.RevalidateAiKeyAsync(user.Id, Claude, TestContext.Current.CancellationToken));
        Assert.Equal(("Invalid", Morning), StatusOfAiKey(application, user.Id));
        Assert.Equal([new Told("invalid", user.Id, "claude", InTransaction: true, StatusThen: "Valid")], dependents.Told);

        // Calls that go on failing do not have the provider asked again and again: once a minute.
        clock.Set(Morning.AddSeconds(59));
        Assert.Equal(KeyCheckOutcome.TooSoon, await validation.RevalidateAiKeyAsync(user.Id, Claude, TestContext.Current.CancellationToken));
        Assert.Single(application.Providers.Asked);

        clock.Set(Morning.AddSeconds(60));
        Assert.Equal(KeyCheckOutcome.Invalid, await validation.RevalidateAiKeyAsync(user.Id, Claude, TestContext.Current.CancellationToken));
        Assert.Equal(2, application.Providers.Asked.Count);

        // A call to Binance that failed finds the key good: what it may do is as Binance says now.
        application.Providers.BinanceAnswer = (_, _) => new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: false);
        Assert.Equal(
            KeyCheckOutcome.Valid, await validation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Demo, TestContext.Current.CancellationToken));

        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        Assert.Equal(("Valid", Morning.AddSeconds(60)), (exchangeKey.Status, exchangeKey.ValidatedAt));
        Assert.Equal((true, false), (exchangeKey.SpotTrading, exchangeKey.FuturesTrading));

        // A provider that gives no answer leaves no time in the row, and is not asked again
        // within the minute either.
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Unreachable;
        clock.Set(Morning.AddMinutes(5));
        Assert.Equal(
            KeyCheckOutcome.Unreachable,
            await validation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Demo, TestContext.Current.CancellationToken));
        Assert.Equal(
            KeyCheckOutcome.TooSoon, await validation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Demo, TestContext.Current.CancellationToken));
        Assert.Equal(("Valid", Morning.AddSeconds(60)), StatusOfExchangeKey(application, user.Id, "Demo"));

        // Where no key is stored, there is nothing to check, and nobody is asked.
        var asked = application.Providers.Asked.Count;
        Assert.Equal(
            KeyCheckOutcome.NoKey, await validation.RevalidateAiKeyAsync(user.Id, new AiProviderCode("grok"), TestContext.Current.CancellationToken));
        Assert.Equal(
            KeyCheckOutcome.NoKey, await validation.RevalidateExchangeKeyAsync(user.Id, TradingMode.Live, TestContext.Current.CancellationToken));
        Assert.Equal(
            KeyCheckOutcome.NoKey, await validation.RevalidateAiKeyAsync(Guid.CreateVersion7(), Claude, TestContext.Current.CancellationToken));
        Assert.Equal(asked, application.Providers.Asked.Count);
    }

    [Fact]
    public async Task A_stored_value_that_cannot_be_read_is_left_as_it_is_and_nobody_is_asked()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(ada.Id, Morning.AddDays(-2));
        await application.GiveKeysAsync(grace.Id, Morning.AddDays(-1));

        // A value that was changed, and so does not decrypt: here, the keys of Ada hold the
        // values of Grace, which are bound to her rows.
        application.Database.Execute(
            $"""
            UPDATE ai_credentials SET encrypted_value = (SELECT encrypted_value FROM ai_credentials WHERE user_id = '{grace.Id}')
            WHERE user_id = '{ada.Id}';
            UPDATE exchange_credentials SET encrypted_value = (SELECT encrypted_value FROM exchange_credentials WHERE user_id = '{grace.Id}')
            WHERE user_id = '{ada.Id}';
            """);

        await application.CheckKeysAsync(clock.GetUtcNow());

        // Ada's keys are as they were, and the keys of Grace were checked all the same: a
        // value that cannot be read does not stand for a provider that is down.
        Assert.Equal(("Valid", Morning.AddDays(-2)), StatusOfAiKey(application, ada.Id));
        Assert.Equal(("Valid", Morning.AddDays(-2)), StatusOfExchangeKey(application, ada.Id, "Demo"));
        Assert.Equal(("Valid", Morning), StatusOfAiKey(application, grace.Id));
        Assert.Equal(("Valid", Morning), StatusOfExchangeKey(application, grace.Id, "Demo"));
        Assert.Equal(["claude", "Demo"], application.Providers.Asked.Select(asked => asked.About));
        Assert.All(application.Providers.Asked, asked => Assert.StartsWith(grace.Id.ToString("N"), asked.Key));

        var unreadable = application.Events().Where(written => written.Area == ValidatorArea).ToList();
        Assert.Equal(
            [
                "The key of user {UserId} for the AI provider {Provider} cannot be read, so it is not checked",
                "The key of user {UserId} of Binance for {TradingMode} cannot be read, so it is not checked",
            ],
            unreadable.Select(written => written.Template));
        Assert.All(unreadable, written => Assert.Equal(("Error", ada.Id.ToString()), (written.Level, written.Text("UserId"))));
    }

    [Fact]
    public async Task A_user_has_a_stored_key_checked_again_with_the_button_of_the_list()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        using var gracesBrowser = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.ConfirmIdentityAsync();
        await gracesBrowser.StoreAiKeyAsync("claude", Keys.Of("grace", "claude"));
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);

        // A key that may not trade on Futures, as its user made it at first.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { FuturesTrading = false };
        var (_, before) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        Assert.Contains("Spot trading: allowed. Futures trading: not allowed.", before);
        Assert.Contains("<button type=\"submit\">Check the Live key again</button>", before);
        Assert.Contains("<button type=\"submit\">Check the Claude key again</button>", before);

        // There is a button for each stored key, and none where no key is stored.
        Assert.Equal(2, CountOf(before, "<button type=\"submit\">Check the "));
        var asked = application.Providers.Asked.Count;

        // The user turns Futures on at Binance, and has the key checked: no proof is asked for
        // it, long after the last one, and what the key may do is shown at once.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive;
        clock.Set(Morning.AddMinutes(30));
        var (path, after) = await client.CheckExchangeKeyAsync("live");

        Assert.Equal(KeysPage, path);
        Assert.Contains("<p role=\"status\">The Binance key for Live was checked again, and is valid.</p>", after);
        Assert.Contains("Spot trading: allowed. Futures trading: allowed.", after);
        Assert.Contains("Valid: Binance took it on 12 November 2026, 09:50 UTC.", after);
        Assert.Equal(new AskedKey("Live", Keys.BinanceApi, Keys.BinanceSecret), Assert.Single(application.Providers.Asked.Skip(asked)));

        // The key of an AI provider that was revoked is found out the same way.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        var (_, refused) = await client.CheckAiKeyAsync("claude");

        Assert.Contains(
            "<p role=\"status\">The Claude key was checked again, and was refused. Nothing can use it until it is taken again.</p>", refused);
        Assert.Contains("Not valid: Claude refused it on 12 November 2026, 09:50 UTC.", KeyBrowser.Read(refused));
        Assert.Equal(("Invalid", Morning.AddMinutes(30)), StatusOfAiKey(application, ada.Id));

        // It is the key of the user who pressed the button: Grace's is not asked about, and is as it was.
        Assert.Equal(new AskedKey("claude", Keys.Claude), application.Providers.Asked[^1]);
        Assert.Equal(("Valid", Morning), StatusOfAiKey(application, grace.Id));

        // Pressed again at once, it asks nobody.
        var (_, tooSoon) = await client.CheckAiKeyAsync("claude");
        Assert.Contains("<p role=\"status\">The Claude key was checked less than a minute ago. Try again in a moment.</p>", tooSoon);
        Assert.Equal(asked + 2, application.Providers.Asked.Count);

        // A provider that gives no answer leaves the key as it was, and the page says so.
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Unreachable;
        clock.Set(Morning.AddMinutes(35));
        var (_, noAnswer) = await client.CheckExchangeKeyAsync("live");
        Assert.Contains(
            "<p role=\"status\">The Binance key for Live could not be checked: no answer came. It is as it was. Try again in a moment.</p>",
            noAnswer);
        Assert.Contains("Valid: Binance took it on 12 November 2026, 09:50 UTC.", noAnswer);

        // A key that can withdraw by now is deleted, and the page says why.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { CanWithdraw = true };
        clock.Set(Morning.AddMinutes(40));
        var (_, deleted) = await client.CheckExchangeKeyAsync("live");

        Assert.Contains("<p role=\"status\">Binance reports that the Binance key for Live can withdraw.", deleted);
        Assert.Contains("so the key was deleted.", deleted);
        Assert.Contains("<a href=\"/account/keys/binance/live\">Add a Live key</a>", deleted);
        Assert.Empty(StoredExchangeCredential.All(application.Database));
        Assert.Equal("KeyRejected", StoredAuditEvent.All(application.Database)[^1].Action);
    }

    [Fact]
    public async Task The_button_checks_nothing_for_a_visitor_and_nothing_where_no_key_is_stored()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        using var visitor = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(KeysPage));
        var asked = application.Providers.Asked.Count;

        // A visitor is sent to sign in, as on every page of an account.
        var (address, _) = await visitor.OpenAsync(KeysPage);
        Assert.StartsWith(SessionAuthentication.SignInPath, address);

        // The list has no form for a key that is not stored, so one that is posted all the
        // same is answered as a form that no page has, and asks nobody.
        using var answer = await client.SendFormAsync(KeysPage, "check-ai-grok", token);
        using var exchangeAnswer = await client.SendFormAsync(KeysPage, "check-binance-live", token);

        Assert.False(answer.IsSuccessStatusCode);
        Assert.False(exchangeAnswer.IsSuccessStatusCode);
        Assert.Equal(asked, application.Providers.Asked.Count);
    }

    private static (string Status, DateTimeOffset? ValidatedAt) StatusOfAiKey(AccountsApplication application, Guid userId)
    {
        var key = StoredAiCredential.Of(application.Database, userId, "claude");

        return (key.Status, key.ValidatedAt);
    }

    private static (string Status, DateTimeOffset? ValidatedAt) StatusOfExchangeKey(
        AccountsApplication application, Guid userId, string environment)
    {
        var key = StoredExchangeCredential.Of(application.Database, userId, environment);

        return (key.Status, key.ValidatedAt);
    }

    private static int CountOf(string page, string text) => page.Split(text).Length - 1;

    /// <summary>One time that <see cref="RecordingDependents"/> was told, with how.</summary>
    /// <param name="What">What it was told of the key: <c>deleted</c> or <c>invalid</c>.</param>
    /// <param name="UserId">The user whose key it is.</param>
    /// <param name="Key">Which key: the code of the provider, or the name of the environment.</param>
    /// <param name="InTransaction">Whether the context it was told with holds a transaction.</param>
    /// <param name="StatusThen">What that context finds as the status of the key, or null when it finds no key.</param>
    private sealed record Told(string What, Guid UserId, string Key, bool InTransaction, string? StatusThen);

    /// <summary>
    /// Stands for Agents, which is not built yet: it is told when a key goes or turns out to be
    /// invalid, keeps how it was told, and fails when a test tells it to.
    /// </summary>
    private sealed class RecordingDependents : IKeyDependents
    {
        private readonly ConcurrentQueue<Told> _told = new();

        /// <summary>Whether it fails, as a change to the agents that cannot be stored would.</summary>
        public bool Fails { get; set; }

        /// <summary>The times it was told, in order.</summary>
        public IReadOnlyList<Told> Told => [.. _told];

        public Task AiKeyDeletedAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken) =>
            TellOfAiKeyAsync("deleted", context, userId, provider, cancellationToken);

        public Task AiKeyInvalidAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken) =>
            TellOfAiKeyAsync("invalid", context, userId, provider, cancellationToken);

        public Task ExchangeKeyDeletedAsync(
            EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
            TellOfExchangeKeyAsync("deleted", context, userId, environment, cancellationToken);

        public Task ExchangeKeyInvalidAsync(
            EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken) =>
            TellOfExchangeKeyAsync("invalid", context, userId, environment, cancellationToken);

        private async Task TellOfAiKeyAsync(
            string what, EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
        {
            var status = await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Provider == provider)
                .Select(credential => (KeyStatus?)credential.Status)
                .SingleOrDefaultAsync(cancellationToken);

            Tell(new Told(what, userId, provider.Value, context.Database.CurrentTransaction is not null, status?.ToString()));
        }

        private async Task TellOfExchangeKeyAsync(
            string what, EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken)
        {
            var status = await context.Set<ExchangeCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Environment == environment)
                .Select(credential => (KeyStatus?)credential.Status)
                .SingleOrDefaultAsync(cancellationToken);

            Tell(new Told(what, userId, environment.ToString(), context.Database.CurrentTransaction is not null, status?.ToString()));
        }

        private void Tell(Told told)
        {
            _told.Enqueue(told);

            if (Fails)
            {
                throw new InvalidOperationException("The agents of the test could not be changed.");
            }
        }
    }
}
