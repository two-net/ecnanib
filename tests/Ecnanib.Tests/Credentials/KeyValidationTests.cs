using Ecnanib.Accounts;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// When a key is saved, the system validates it with a read-only call and records its status,
/// detected permissions, and validation time (FR-KEY-3). Only a key that its provider takes is
/// stored: one that is refused is not, and neither is one whose provider gave no answer. The key
/// is shown to its provider and to nothing else (FR-KEY-8).
/// </summary>
public class KeyValidationTests
{
    private const string Ada = "ada@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;
    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;

    private const string StoredKeysArea = "Ecnanib.Credentials.StoredKeys";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_key_is_shown_to_its_provider_when_it_is_saved_and_stored_as_valid_with_the_time()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // As it was typed, without the spaces around it.
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", $"  {Keys.Claude}\n")).Path);
        clock.Set(Morning.AddMinutes(3));
        Assert.Equal(KeysPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);

        // Each was asked about once, with the key, where it belongs.
        Assert.Equal(
            [new AskedKey("claude", Keys.Claude), new AskedKey("Demo", Keys.BinanceApi, Keys.BinanceSecret)],
            application.Providers.Asked);

        // The status and the time of the check are stored beside the key.
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal(("Valid", Morning), (aiKey.Status, aiKey.ValidatedAt));

        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        Assert.Equal(("Valid", Morning.AddMinutes(3)), (exchangeKey.Status, exchangeKey.ValidatedAt));
        Assert.Equal((true, true, null), (exchangeKey.SpotTrading, exchangeKey.FuturesTrading, exchangeKey.IpRestricted));

        // And the list says so.
        var list = KeyBrowser.Read(await client.PageAsync(KeysPage));
        Assert.Contains("Valid: Claude took it on 12 November 2026, 09:00 UTC.", list);
        Assert.Contains("Valid: Binance took it on 12 November 2026, 09:03 UTC.", list);
        Assert.Contains("Spot trading: allowed. Futures trading: allowed.", list);
    }

    [Fact]
    public async Task A_key_that_its_provider_refuses_is_not_stored_and_the_page_says_so()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Refused;
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Refused;

        var (path, page) = await client.StoreAiKeyAsync("claude", Keys.Claude);
        var (exchangePath, exchangePage) = await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // The answer is the page with the form, which says why and does not repeat the key.
        Assert.Equal((KeyBrowser.AiKeyPage("claude"), KeyBrowser.ExchangeKeyPage("live")), (path, exchangePath));
        Assert.Contains("role=\"alert\">Claude did not take this key, so nothing was stored.", KeyBrowser.Read(page));
        Assert.Contains("name=\"ApiKey\"", page);
        Assert.Contains("role=\"alert\">Binance did not take these keys, so nothing was stored.", KeyBrowser.Read(exchangePage));
        Assert.Contains("keys of your real Binance account", KeyBrowser.Read(exchangePage));
        Assert.All(
            new[] { page, exchangePage },
            answer => Assert.All(
                new[] { Keys.Claude, Keys.BinanceApi, Keys.BinanceSecret }, key => Assert.DoesNotContain(key, answer)));

        // A key that works only from listed addresses is refused from any other, so the page
        // has the addresses of the service beside the answer (FR-KEY-4).
        Assert.Contains("<li>203.0.113.7</li>", exchangePage);

        // Nothing was stored, and nothing recorded: no key was added.
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Key", StringComparison.Ordinal));
        Assert.Contains("No key stored.", await client.PageAsync(KeysPage));

        // The log says what was refused, by the user and the provider or the environment.
        Assert.Equal(
            [
                "User {UserId} was refused storing a key of the AI provider {Provider}: the provider did not take it",
                "User {UserId} was refused storing a key of Binance for {TradingMode}: Binance did not take it",
            ],
            application.Events().Where(written => written.Area == StoredKeysArea).Select(written => written.Template));
        Assert.All(
            application.Events().Where(written => written.Area == StoredKeysArea),
            written => Assert.Equal(user.Id.ToString(), written.Text("UserId")));
    }

    [Fact]
    public async Task A_provider_that_gives_no_answer_leaves_nothing_stored_and_the_user_tries_again()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Unreachable;
        application.Providers.BinanceAnswer = (_, _) => ExchangeKeyCheck.Unreachable;

        var (path, page) = await client.StoreAiKeyAsync("gemini", Keys.Claude);
        var (exchangePath, exchangePage) = await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);

        // It is not known whether the key is good, so it is not stored as one that is.
        Assert.Equal((KeyBrowser.AiKeyPage("gemini"), KeyBrowser.ExchangeKeyPage("demo")), (path, exchangePath));
        Assert.Contains(
            "role=\"alert\">Gemini gave no answer, so the key could not be checked and nothing was stored. Try again in a moment.", page);
        Assert.Contains(
            "role=\"alert\">Binance gave no answer, so the keys could not be checked and nothing was stored. Try again in a moment.",
            exchangePage);
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
        Assert.Equal(
            [
                "User {UserId} could not store a key of the AI provider {Provider}: the provider gave no answer",
                "User {UserId} could not store a key of Binance for {TradingMode}: Binance gave no answer",
            ],
            application.Events().Where(written => written.Area == StoredKeysArea).Select(written => written.Template));

        // Once the provider answers, the same keys are stored.
        application.Providers.AiAnswer = (_, _) => KeyVerdict.Accepted;
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo;

        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gemini", Keys.Claude)).Path);
        Assert.Equal(KeysPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);
        Assert.Equal(["gemini"], StoredAiCredential.All(application.Database).Select(key => key.Provider));
        Assert.Equal(["Demo"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));
    }

    [Theory]
    [InlineData(KeyVerdict.Refused)]
    [InlineData(KeyVerdict.Unreachable)]
    public async Task A_key_that_is_not_taken_in_place_of_a_stored_one_leaves_the_stored_one_as_it_was(KeyVerdict answer)
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var stored = application.RowsOf(user.Id);

        // Only the keys that are stored are taken: whatever comes after them is not.
        application.Providers.AiAnswer = (_, key) => key == Keys.Claude ? KeyVerdict.Accepted : answer;
        application.Providers.BinanceAnswer =
            (_, key) => key == Keys.BinanceApi ? StandInProviders.TakenInDemo : new ExchangeKeyCheck(answer);
        clock.Set(Morning.AddMinutes(2));

        Assert.Equal(KeyBrowser.AiKeyPage("claude"), (await client.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Path);
        Assert.Equal(
            KeyBrowser.ExchangeKeyPage("demo"),
            (await client.StoreExchangeKeyAsync("demo", Keys.Of("another", "demo"), Keys.Of("another-sealed", "demo"))).Path);

        // Every row of a key is as it was, to the last column: the key, its status, and the time.
        Assert.Equal(TextOf(stored), TextOf(application.RowsOf(user.Id)));
        Assert.Contains("<code>••••Wx7Z</code>", KeyBrowser.Read(await client.PageAsync(KeysPage)));

        // And nothing was recorded: no key was replaced.
        Assert.Equal(
            ["KeyAdded", "KeyAdded"],
            StoredAuditEvent.All(application.Database)
                .Select(recorded => recorded.Action)
                .Where(action => action.StartsWith("Key", StringComparison.Ordinal)));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(Environment.NewLine, rows["ai_credentials"].Concat(rows["exchange_credentials"]));
    }

    [Fact]
    public async Task A_key_that_takes_the_place_of_an_invalid_one_is_valid_as_of_its_own_check()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        // The stored keys turn out to be refused, later.
        application.Database.Execute("UPDATE ai_credentials SET status = 'Invalid'; UPDATE exchange_credentials SET status = 'Invalid'");
        application.Providers.BinanceAnswer =
            (_, _) => new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: false, FuturesTrading: true, CanWithdraw: false, IpRestricted: false);
        clock.Set(Morning.AddMinutes(5));

        await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        await client.StoreExchangeKeyAsync("live", Keys.Of("another", "live"), Keys.Of("another-sealed", "live"));

        // The row is the same one, and says what was found about the key that is in it now.
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        Assert.Equal(("Qr5T", "Valid", Morning.AddMinutes(5)), (aiKey.MaskedSuffix, aiKey.Status, aiKey.ValidatedAt));

        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Live");
        Assert.Equal(("live", "Valid", Morning.AddMinutes(5)), (exchangeKey.MaskedSuffix, exchangeKey.Status, exchangeKey.ValidatedAt));
        Assert.Equal((false, true, false), (exchangeKey.SpotTrading, exchangeKey.FuturesTrading, exchangeKey.IpRestricted));
    }

    [Fact]
    public async Task No_provider_is_asked_about_what_a_user_may_not_store_or_what_cannot_be_a_key()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        // Without the proof of who the user is, a form that is posted all the same reaches no provider.
        Assert.Equal(
            ConfirmPage, (await client.PostAsync(KeyBrowser.AiKeyPage("claude"), KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude))).Path);
        Assert.Equal(
            ConfirmPage,
            (await client.PostAsync(
                KeyBrowser.ExchangeKeyPage("demo"), KeyBrowser.StoreForm, token, ("ApiKey", Keys.BinanceApi), ("SecretKey", Keys.BinanceSecret))).Path);

        // With it, what cannot be a key reaches none either, and neither does a key for a
        // provider that does not exist.
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", "too short");
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, "too short");
        await client.PostAsync("/account/keys/ai/nobody", KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude));

        Assert.Empty(application.Providers.Asked);
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task No_provider_is_asked_for_an_account_whose_address_is_not_verified()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(KeysPage));

        await client.PostAsync(KeyBrowser.AiKeyPage("claude"), KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude));
        await client.PostAsync(
            KeyBrowser.ExchangeKeyPage("demo"), KeyBrowser.StoreForm, token, ("ApiKey", Keys.BinanceApi), ("SecretKey", Keys.BinanceSecret));

        // Such an account may not be its user's to keep, and a key that someone planted in it is
        // not shown to a provider either.
        Assert.Empty(application.Providers.Asked);
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task A_key_is_not_stored_when_the_proof_ended_while_its_provider_was_asked()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The provider takes its time, and the proof of ten minutes ago no longer counts when
        // it answers. Whether the user may store a key is asked again, under the lock that
        // holds until the key is stored (FR-ACC-4).
        application.Providers.WhileAsked = () =>
        {
            clock.Set(Morning.AddMinutes(11));

            return Task.CompletedTask;
        };

        var (path, _) = await client.StoreAiKeyAsync("claude", Keys.Claude);

        Assert.Equal(ConfirmPage, path);
        Assert.Equal([new AskedKey("claude", Keys.Claude)], application.Providers.Asked);
        Assert.Empty(StoredAiCredential.All(application.Database));

        // And the same for Binance.
        await client.ConfirmIdentityAsync();
        application.Providers.WhileAsked = () =>
        {
            clock.Set(Morning.AddMinutes(22));

            return Task.CompletedTask;
        };

        Assert.Equal(ConfirmPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task A_key_is_in_no_log_event_whatever_its_provider_says_about_it()
    {
        // With everything that can be logged: the debug level, for every area.
        using var application = new AccountsApplication(
            settings: file => file.With("Logging:MinimumLevel", "Debug").Without("Logging:Overrides:Microsoft.AspNetCore"));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        string[] keys = [Keys.Claude, Keys.AnotherClaude, Keys.BinanceApi, Keys.BinanceSecret];

        foreach (var answer in new[] { KeyVerdict.Refused, KeyVerdict.Unreachable, KeyVerdict.Accepted })
        {
            application.Providers.AiAnswer = (_, _) => answer;
            application.Providers.BinanceAnswer =
                (_, _) => answer == KeyVerdict.Accepted ? StandInProviders.TakenInLive : new ExchangeKeyCheck(answer);

            await client.StoreAiKeyAsync("claude", Keys.Claude);
            await client.StoreAiKeyAsync("grok", Keys.AnotherClaude);
            await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        }

        // And one that Binance reports as able to withdraw.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInLive with { CanWithdraw = true };
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);

        Assert.Equal(10, application.Providers.Asked.Count);

        var log = application.LogText();
        Assert.All(keys.SelectMany(Keys.FormsOf), form => Assert.DoesNotContain(form, log));

        // Nor are the last characters of one, which the pages show.
        Assert.All(
            application.Events().Where(written => written.Area?.StartsWith("Ecnanib.Credentials", StringComparison.Ordinal) == true),
            written => Assert.All(new[] { "Wx7Z", "Qr5T", "Lm3N", "Zz9Y" }, shown => Assert.DoesNotContain(shown, written.ToString())));
    }
}
