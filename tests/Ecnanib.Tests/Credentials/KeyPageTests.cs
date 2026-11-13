using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Account;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// A user stores their own key for each AI provider and one Binance credential for each
/// environment (FR-KEY-1, FR-KEY-2). A key is write-only: after saving, only its last four
/// characters are shown (FR-KEY-8). A user can replace or delete any key at any time, and
/// deletion is permanent (FR-KEY-10). Adding or replacing a key takes a fresh proof of who the
/// user is (FR-ACC-4).
/// </summary>
public class KeyPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string KeysPage = KeyBrowser.KeysPage;
    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;

    private const string NothingStored = "No key stored.";
    private const string AskedTemplate = "User {UserId} is asked to confirm who they are before {Action}";
    private const string NotVerifiedTemplate = "User {UserId} was refused storing a key: their email address is not verified";

    private const string GoneMeanwhile = "/gone-meanwhile";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Providers = ["claude", "gemini", "gpt", "grok"];
    private static readonly string[] Environments = ["demo", "live"];

    [Fact]
    public async Task A_user_stores_one_key_for_each_AI_provider_and_one_Binance_credential_for_each_environment()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page has a place for each provider and for each environment, and all are empty.
        var empty = KeyBrowser.Read(await client.PageAsync(KeysPage));

        Assert.Contains("<h1>Your API keys</h1>", empty);
        Assert.All(
            new[] { "Claude", "Gemini", "GPT", "Grok", "Demo", "Live" },
            place => Assert.Contains($"<strong>{place}</strong>", empty));
        Assert.Equal(6, Regex.Count(empty, Regex.Escape(NothingStored)));
        Assert.Contains("<a href=\"/account/keys/ai/claude\">Add a Claude key</a>", empty);
        Assert.Contains("<a href=\"/account/keys/binance/demo\">Add a Demo key</a>", empty);

        // The account page leads there.
        Assert.Contains("<a href=\"/account/keys\">Manage your API keys</a>", await client.PageAsync("/account"));

        await client.ConfirmIdentityAsync();

        // A key for each, and each answer leads back to the page that says what is stored.
        foreach (var provider in Providers)
        {
            Assert.Equal(KeysPage, (await client.StoreAiKeyAsync(provider, Keys.Of("ada", provider))).Path);
        }

        foreach (var environment in Environments)
        {
            Assert.Equal(
                KeysPage,
                (await client.StoreExchangeKeyAsync(environment, Keys.Of("ada", environment), Keys.Of("ada-sealed", environment))).Path);
        }

        var aiKeys = StoredAiCredential.All(application.Database);
        var exchangeKeys = StoredExchangeCredential.All(application.Database);

        Assert.Equal(Providers, aiKeys.Select(key => key.Provider));
        Assert.Equal(["Demo", "Live"], exchangeKeys.Select(key => key.Environment));
        Assert.All(aiKeys, key => Assert.Equal((user.Id, Morning), (key.UserId, key.SavedAt)));
        Assert.All(exchangeKeys, key => Assert.Equal((user.Id, Morning), (key.UserId, key.SavedAt)));

        // Each place shows its key now, by its last four characters, with what can be done about it.
        var full = KeyBrowser.Read(await client.PageAsync(KeysPage));

        Assert.DoesNotContain(NothingStored, full);
        Assert.All(Providers, provider => Assert.Contains($"<code>{KeyBrowser.Masked(Keys.Of("ada", provider))}</code>", full));
        Assert.All(Environments, environment => Assert.Contains($"<code>{KeyBrowser.Masked(Keys.Of("ada", environment))}</code>", full));
        Assert.Contains("saved 12 November 2026, 09:00 UTC", full);
        Assert.Contains("<a href=\"/account/keys/ai/claude\">Replace the Claude key</a>", full);
        Assert.Contains("<a href=\"/account/keys/ai/claude/delete\">Delete the Claude key</a>", full);
        Assert.Contains("<a href=\"/account/keys/binance/live\">Replace the Live key</a>", full);
        Assert.Contains("<a href=\"/account/keys/binance/live/delete\">Delete the Live key</a>", full);

        // A second key for a provider or an environment takes the place of the first: there is
        // still one for each.
        await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);

        Assert.Equal(Providers, StoredAiCredential.All(application.Database).Select(key => key.Provider));
        Assert.Equal(["Demo", "Live"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));
    }

    [Fact]
    public async Task After_saving_a_key_is_shown_only_by_its_last_four_characters()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The answers that saved the keys, and every page that shows them afterwards: the list,
        // and the pages that replace and delete each.
        string[] pages =
        [
            (await client.StoreAiKeyAsync("claude", Keys.Claude)).Page,
            (await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret)).Page,
            await client.PageAsync(KeysPage),
            await client.PageAsync(KeyBrowser.AiKeyPage("claude")),
            await client.PageAsync(KeyBrowser.DeleteAiKeyPage("claude")),
            await client.PageAsync(KeyBrowser.ExchangeKeyPage("live")),
            await client.PageAsync(KeyBrowser.DeleteExchangeKeyPage("live")),
        ];

        Assert.All(pages, page =>
        {
            var read = KeyBrowser.Read(page);

            // Dots and four characters, which is all of a key that a page ever holds.
            Assert.Matches("<code>••••(Wx7Z|Lm3N)</code>", read);

            // Not the keys, and not a part of one that is longer than its end.
            Assert.DoesNotContain(Keys.Claude, read);
            Assert.DoesNotContain(Keys.Claude[^5..], read);
            Assert.DoesNotContain(Keys.BinanceApi, read);
            Assert.DoesNotContain(Keys.BinanceApi[^5..], read);

            // Of the secret key of Binance there is nothing at all, not even its end.
            Assert.DoesNotContain(Keys.BinanceSecret, read);
            Assert.DoesNotContain(Keys.BinanceSecret[^4..], read);

            // And no field that takes a key comes back filled in.
            Assert.All(
                Regex.Matches(page, "<input[^>]*name=\"(?:ApiKey|SecretKey)\"[^>]*>"),
                field => Assert.DoesNotContain("value=", field.Value));
        });

        // The pages that take a key ask the browser not to keep it either.
        Assert.Contains("name=\"ApiKey\" type=\"password\" autocomplete=\"off\"", pages[3]);
        Assert.Contains("name=\"SecretKey\" type=\"password\" autocomplete=\"off\"", pages[5]);

        // What is stored beside the encrypted key is its last four characters, and no more.
        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.Equal("Wx7Z", StoredAiCredential.Of(application.Database, user.Id, "claude").MaskedSuffix);
        Assert.Equal("Lm3N", StoredExchangeCredential.Of(application.Database, user.Id, "Live").MaskedSuffix);
    }

    [Fact]
    public async Task A_key_is_never_sent_to_the_browser_logged_or_stored_readable()
    {
        // With the log at its most talkative, the framework's own events included, so that
        // nothing which would hold a key is left out.
        using var application = new AccountsApplication(
            settings: file => file.With("Logging:MinimumLevel", "Debug").Without("Logging:Overrides:Microsoft.AspNetCore"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        string[] keys = [Keys.Claude, Keys.AnotherClaude, Keys.BinanceApi, Keys.BinanceSecret];
        const string notAKey = "typed with spaces MARKER-0123456789";

        // Everything a user does with keys: adding, entering what is not a key, replacing, and
        // looking at what is stored.
        var seen = new List<string>
        {
            (await client.StoreAiKeyAsync("claude", Keys.Claude)).Page,
            (await client.StoreAiKeyAsync("claude", notAKey)).Page,
            (await client.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Page,
            (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Page,
            (await client.StoreExchangeKeyAsync("demo", notAKey, notAKey)).Page,
            await client.PageAsync(KeysPage),
            await client.PageAsync("/account"),
            await client.PageAsync("/account/export"),
        };

        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");
        var everything = application.EverythingStored();

        // And deleting.
        seen.Add((await client.DeleteAiKeyAsync("claude")).Page);
        seen.Add((await client.DeleteExchangeKeyAsync("demo")).Page);

        // No answer held a key, or what was typed in place of one.
        Assert.All(seen, page => Assert.All(keys.Append(notAKey), typed => Assert.DoesNotContain(typed, page)));

        // The database held the keys encrypted, and in no form that anyone could read.
        Assert.Contains(Convert.ToHexString(aiKey.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Convert.ToHexString(exchangeKey.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.All(keys.SelectMany(Keys.FormsOf), form => Assert.DoesNotContain(form, everything));

        // The log says what was done, by the user and the provider or the environment.
        var events = application.Events();
        Assert.Equal(
            [
                "User {UserId} added a key of the AI provider {Provider}",
                "User {UserId} replaced their key of the AI provider {Provider}",
                "User {UserId} added a key of Binance for {TradingMode}",
                "User {UserId} deleted their key of the AI provider {Provider}",
                "User {UserId} deleted their key of Binance for {TradingMode}",
            ],
            events.Where(written => written.Area == "Ecnanib.Credentials.StoredKeys").Select(written => written.Template));
        Assert.All(
            events.Where(written => written.Area == "Ecnanib.Credentials.StoredKeys"),
            written => Assert.Equal(user.Id.ToString(), written.Text("UserId")));
        Assert.Contains(events, written => written.Text("Provider") == "claude");
        Assert.Contains(events, written => written.Text("TradingMode") == "Demo");

        // It is the log of everything: what the framework says about each request is in it.
        Assert.Contains(
            events,
            written => written.Level == "Debug" && written.Area?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);

        // And it holds no key, in any form, nor what was typed in place of one.
        var log = application.LogText();
        Assert.All(keys.SelectMany(Keys.FormsOf), form => Assert.DoesNotContain(form, log));
        Assert.DoesNotContain("MARKER", log);
    }

    [Theory]
    [InlineData("", "Enter the key.")]
    [InlineData("short-MARKER", "That is too short to be a key. A key has 16 characters or more.")]
    [InlineData("a key with a space in it MARKER", "A key is in one piece")]
    [InlineData("a-key-with-a-letter-that-no-key-has-é-MARKER", "A key is in one piece")]
    public async Task What_cannot_be_a_key_is_not_stored_and_the_page_says_why_without_showing_it(string typed, string message)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // For an AI provider.
        var (path, page) = await client.StoreAiKeyAsync("claude", typed);

        Assert.Equal(KeyBrowser.AiKeyPage("claude"), path);
        Assert.Contains($"<p class=\"field-error\" role=\"alert\">{message}", page);
        Assert.Contains("aria-invalid=\"true\"", page);
        Assert.DoesNotContain("MARKER", page);
        Assert.Empty(StoredAiCredential.All(application.Database));

        // For Binance, where each of the two keys is told what is wrong with it.
        var (exchangePath, exchangePage) = await client.StoreExchangeKeyAsync("demo", typed, typed);

        Assert.Equal(KeyBrowser.ExchangeKeyPage("demo"), exchangePath);
        Assert.Equal(2, Regex.Count(exchangePage, Regex.Escape($"<p class=\"field-error\" role=\"alert\">{message}")));
        Assert.DoesNotContain("MARKER", exchangePage);

        // One of the two is not enough either: nothing is stored of a credential that is half there.
        var (_, halfPage) = await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, typed);

        Assert.Equal(1, Regex.Count(halfPage, "class=\"field-error\""));
        Assert.DoesNotContain(Keys.BinanceApi, halfPage);
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task A_key_is_stored_without_the_spaces_around_it_and_one_that_is_too_long_is_not()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // As a key is pasted: with a line break after it.
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", $"  {Keys.Claude}\r\n")).Path);
        Assert.Equal("Wx7Z", StoredAiCredential.Of(application.Database, user.Id, "claude").MaskedSuffix);

        // 512 characters are a key, and 513 are not.
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("gemini", new string('k', 512))).Path);

        var (path, page) = await client.StoreAiKeyAsync("gpt", new string('k', 513));

        Assert.Equal(KeyBrowser.AiKeyPage("gpt"), path);
        Assert.Contains("That is too long to be a key. A key has 512 characters at most.", page);
        Assert.Equal(["claude", "gemini"], StoredAiCredential.All(application.Database).Select(key => key.Provider));
    }

    [Fact]
    public async Task A_key_is_not_added_until_its_user_has_proved_again_who_they_are()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Being signed in is not enough to be shown a page that takes a key: the browser is
        // sent to the page that takes the proof, which leads back.
        string[] pages = [KeyBrowser.AiKeyPage("claude"), KeyBrowser.ExchangeKeyPage("demo")];

        foreach (var page in pages)
        {
            var (asked, shown) = await client.OpenAsync(page);

            Assert.Equal($"{ConfirmPage}?returnUrl={Uri.EscapeDataString(page)}", asked);
            Assert.Contains("<h1>Confirm that it is you</h1>", shown);
            Assert.DoesNotContain("name=\"ApiKey\"", shown);
        }

        // The forms are posted all the same, as something that is not a page of the application
        // can, with a token against request forgery of the user's own. The check is in the
        // action: nothing is stored, and the browser is sent to the page that takes the proof.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));

        Assert.Equal(
            ConfirmPage,
            (await client.PostAsync(pages[0], KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude))).Path);
        Assert.Equal(
            ConfirmPage,
            (await client.PostAsync(
                pages[1], KeyBrowser.StoreForm, token, ("ApiKey", Keys.BinanceApi), ("SecretKey", Keys.BinanceSecret))).Path);

        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));

        // The log says what was held back, each time.
        var held = application.Events().Where(written => written.Template == AskedTemplate).ToList();
        Assert.Equal(4, held.Count);
        Assert.All(held, written =>
        {
            Assert.Equal(user.Id.ToString(), written.Text("UserId"));
            Assert.Equal(nameof(SensitiveAction.AddOrReplaceKey), written.Text("Action"));
        });

        // The proof leads back to the page, which is shown now. Seeing it stores nothing.
        var (back, form) = await client.ConfirmIdentityAsync(
            page: $"{ConfirmPage}?returnUrl={Uri.EscapeDataString(pages[0])}");

        Assert.Equal(pages[0], back);
        Assert.Contains("<h1>Add your Claude key</h1>", form);
        Assert.Contains("name=\"ApiKey\"", form);
        Assert.Empty(StoredAiCredential.All(application.Database));

        // And the keys are stored when they are entered.
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.Equal(KeysPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);
        Assert.Single(StoredAiCredential.All(application.Database));
        Assert.Single(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task A_key_is_not_replaced_on_a_proof_that_is_older_than_ten_minutes_or_of_another_browser()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await desk.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await desk.ConfirmIdentityAsync();

        await desk.StoreAiKeyAsync("claude", Keys.Claude);
        await desk.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Live");

        // The other browser of the same user gave no proof: it is asked itself, and replaces nothing.
        Assert.Equal(ConfirmPage, (await phone.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Path);
        Assert.Equal(ConfirmPage, (await phone.StoreExchangeKeyAsync("live", Keys.Claude, Keys.AnotherClaude)).Path);

        // Just inside the ten minutes, the browser that gave the proof still replaces a key.
        clock.Set(Morning.Add(ReauthenticationRules.HoldsFor).AddSeconds(-1));
        Assert.Equal(KeysPage, (await desk.StoreAiKeyAsync("gemini", Keys.Of("ada", "gemini"))).Path);

        // After them it does not: the page is not shown, and a form that is posted stores nothing.
        clock.Set(Morning.Add(ReauthenticationRules.HoldsFor));
        Assert.StartsWith(ConfirmPage, (await desk.OpenAsync(KeyBrowser.AiKeyPage("claude"))).Address);
        Assert.Equal(ConfirmPage, (await desk.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Path);
        Assert.Equal(ConfirmPage, (await desk.StoreExchangeKeyAsync("live", Keys.Claude, Keys.AnotherClaude)).Path);

        // What was stored is what is stored, to the last byte.
        var aiKeyNow = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKeyNow = StoredExchangeCredential.Of(application.Database, user.Id, "Live");

        Assert.Equal(Convert.ToHexString(aiKey.EncryptedValue), Convert.ToHexString(aiKeyNow.EncryptedValue));
        Assert.Equal((aiKey.MaskedSuffix, aiKey.SavedAt), (aiKeyNow.MaskedSuffix, aiKeyNow.SavedAt));
        Assert.Equal(Convert.ToHexString(exchangeKey.EncryptedValue), Convert.ToHexString(exchangeKeyNow.EncryptedValue));
        Assert.Equal((exchangeKey.MaskedSuffix, exchangeKey.SavedAt), (exchangeKeyNow.MaskedSuffix, exchangeKeyNow.SavedAt));

        // A new proof, and the key is replaced.
        await desk.ConfirmIdentityAsync();
        Assert.Equal(KeysPage, (await desk.StoreAiKeyAsync("claude", Keys.AnotherClaude)).Path);
        Assert.Equal("Qr5T", StoredAiCredential.Of(application.Database, user.Id, "claude").MaskedSuffix);
    }

    [Fact]
    public async Task A_key_is_not_stored_for_an_account_whose_address_is_not_verified()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        var user = StoredUser.Of(application.Database, Ada)!;

        // Such a user can sign in, and prove who they are. But the address may not be theirs:
        // its owner can still take the account over, and would take a key with it.
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The list says why, and leads to no form.
        var list = await client.PageAsync(KeysPage);

        Assert.Contains($"<p role=\"status\">{KeyPages.VerifyFirst}</p>", list);
        Assert.Equal(6, Regex.Count(list, Regex.Escape(NothingStored)));
        Assert.DoesNotContain("Add a ", list);

        // The pages that take a key say the same, and have no form either.
        string[] pages = [KeyBrowser.AiKeyPage("claude"), KeyBrowser.ExchangeKeyPage("demo")];

        foreach (var page in pages)
        {
            var (address, shown) = await client.OpenAsync(page);

            Assert.Equal(page, address);
            Assert.Contains($"<p role=\"status\">{KeyPages.VerifyFirst}</p>", shown);
            Assert.DoesNotContain("name=\"ApiKey\"", shown);
        }

        // The forms are posted all the same, as something that is not a page of the application
        // can. The check is in the action: nothing is stored, and the answer says why.
        var token = Browser.AntiforgeryTokenIn(list);
        var (path, answer) = await client.PostAsync(pages[0], KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude));
        var (exchangePath, exchangeAnswer) = await client.PostAsync(
            pages[1], KeyBrowser.StoreForm, token, ("ApiKey", Keys.BinanceApi), ("SecretKey", Keys.BinanceSecret));

        Assert.Equal((pages[0], pages[1]), (path, exchangePath));
        Assert.All(new[] { answer, exchangeAnswer }, page =>
        {
            Assert.Contains(KeyPages.VerifyFirst, page);
            Assert.DoesNotContain(Keys.Claude, page);
            Assert.DoesNotContain(Keys.BinanceApi, page);
        });
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));

        // The log says what was refused, both times, and the audit trail has no key in it.
        var refused = application.Events().Where(written => written.Template == NotVerifiedTemplate).ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, written => Assert.Equal(user.Id.ToString(), written.Text("UserId")));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Key", StringComparison.Ordinal));

        // Once the address is verified, with the link it was sent, the key is stored.
        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[^1])));

        Assert.Contains("<a href=\"/account/keys/ai/claude\">Add a Claude key</a>", await client.PageAsync(KeysPage));
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.Equal(KeysPage, (await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret)).Path);
        Assert.Single(StoredAiCredential.All(application.Database));
        Assert.Single(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task Forms_that_arrive_together_are_taken_one_after_the_other()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The form of a place that is empty, six times at once, as from a button that is pressed
        // again and again. Each is taken after the one before it: the first adds the key, and
        // the others replace it. None fails for finding a key where it was about to put one.
        var page = KeyBrowser.AiKeyPage("claude");
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(page));

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            client.PostAsync(page, KeyBrowser.StoreForm, token, ("ApiKey", Keys.Of($"ada-{index}", "claude")))));

        Assert.All(answers, answer => Assert.Equal(KeysPage, answer.Path));

        var stored = Assert.Single(StoredAiCredential.All(application.Database));
        Assert.Equal(
            [
                "KeyAdded", "KeyReplaced", "KeyReplaced", "KeyReplaced", "KeyReplaced", "KeyReplaced",
            ],
            KeyEvents(application).Select(recorded => recorded.Action));
        Assert.All(KeyEvents(application), recorded => Assert.Equal($"ai-key:{stored.Id}", recorded.Target));

        // The same for the two keys of Binance.
        var exchangePage = KeyBrowser.ExchangeKeyPage("live");

        var exchangeAnswers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            client.PostAsync(
                exchangePage,
                KeyBrowser.StoreForm,
                token,
                ("ApiKey", Keys.Of($"ada-{index}", "live")),
                ("SecretKey", Keys.Of($"ada-sealed-{index}", "live")))));

        Assert.All(exchangeAnswers, answer => Assert.Equal(KeysPage, answer.Path));
        Assert.Single(StoredExchangeCredential.All(application.Database));

        // And the button that deletes, four times at once: the key goes once, and is recorded once.
        var deleted = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsync(KeyBrowser.DeleteAiKeyPage("claude"), KeyBrowser.DeleteForm, token)));

        Assert.All(deleted, answer => Assert.Equal(KeysPage, answer.Path));
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Single(KeyEvents(application), recorded => recorded.Action == "KeyDeleted");

        static IReadOnlyList<StoredAuditEvent> KeyEvents(AccountsApplication application) =>
            [.. StoredAuditEvent.All(application.Database).Where(recorded => recorded.Target.StartsWith("ai-key:", StringComparison.Ordinal))];
    }

    [Fact]
    public async Task A_request_that_is_under_way_when_its_account_goes_is_answered_and_stores_nothing()
    {
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(GoneMeanwhileAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);

        // The account is deleted, on another browser, after the request was signed in. What is
        // asked about keys is answered as for someone who has none and may store none, and
        // deleting finds nothing to delete. Nothing fails.
        Assert.Equal(
            "verified: False, keys: 0, storing: ConfirmationNeeded and ConfirmationNeeded, deleting: done",
            await client.PageAsync(GoneMeanwhile));

        // The keys of the account went with it, and none came back.
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    public async Task A_second_key_takes_the_place_of_the_first_which_is_gone()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var first = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var firstExchange = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");

        // The page that replaces a key says which one is stored, by its end, and that it goes.
        var page = KeyBrowser.Read(await client.PageAsync(KeyBrowser.AiKeyPage("claude")));

        Assert.Contains("<h1>Replace your Claude key</h1>", page);
        Assert.Contains("The key that is stored now is <code>••••Wx7Z</code>.", page);

        clock.Set(Morning.AddMinutes(5));
        var (path, list) = await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));

        Assert.Equal(KeysPage, path);
        Assert.Contains("<code>••••Qr5T</code>", KeyBrowser.Read(list));
        Assert.DoesNotContain("Wx7Z", list);

        // The row is the same one, so that what points at the key still does, and it holds the
        // new key from the moment it was saved.
        var second = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var secondExchange = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(("Qr5T", Morning.AddMinutes(5)), (second.MaskedSuffix, second.SavedAt));
        Assert.Equal(firstExchange.Id, secondExchange.Id);
        Assert.Equal(("demo", Morning.AddMinutes(5)), (secondExchange.MaskedSuffix, secondExchange.SavedAt));

        // The first key is nowhere any more, encrypted or not.
        var everything = application.EverythingStored();
        Assert.DoesNotContain(Convert.ToHexString(first.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(firstExchange.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_key_takes_no_proof_and_is_for_good()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreAiKeyAsync("grok", Keys.Of("ada", "grok"));
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("live", Keys.Of("ada", "live"), Keys.Of("ada-sealed", "live"));
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Demo");

        // Long after the proof, which holds for ten minutes: only adding and replacing a key ask for one.
        clock.Set(Morning.AddMinutes(30));

        // The page says which key goes, by its end, and offers to keep it. Seeing it deletes nothing.
        var (address, page) = await client.OpenAsync(KeyBrowser.DeleteAiKeyPage("claude"));

        Assert.Equal(KeyBrowser.DeleteAiKeyPage("claude"), address);
        Assert.Contains("<h1>Delete your Claude key</h1>", page);
        Assert.Contains("This deletes the key <code>••••Wx7Z</code>, at once and for good.", KeyBrowser.Read(page));
        Assert.Contains("<button type=\"submit\">Delete the key</button>", page);
        Assert.Contains("<a href=\"/account/keys\">Keep the key</a>", page);
        Assert.Equal(2, StoredAiCredential.All(application.Database).Count);

        var exchangePage = KeyBrowser.Read(await client.PageAsync(KeyBrowser.DeleteExchangeKeyPage("demo")));

        Assert.Contains("<h1>Delete your Binance keys for Demo</h1>", exchangePage);
        Assert.Contains("This deletes the API key <code>••••Lm3N</code> and its secret key", exchangePage);
        Assert.Contains("<a href=\"/account/keys\">Keep the keys</a>", exchangePage);

        // The button deletes the key, and leads back to the page that says what is stored.
        var (path, list) = await client.DeleteAiKeyAsync("claude");
        await client.DeleteExchangeKeyAsync("demo");

        Assert.Equal(KeysPage, path);
        Assert.DoesNotContain("Wx7Z", list);
        Assert.Contains("<a href=\"/account/keys/ai/claude\">Add a Claude key</a>", list);

        // The rows are gone, and with them the encrypted keys: nothing of either is anywhere.
        // The other keys of the user are as they were.
        Assert.Equal(["grok"], StoredAiCredential.All(application.Database).Select(key => key.Provider));
        Assert.Equal(["Live"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));

        var everything = application.EverythingStored();
        Assert.DoesNotContain(Convert.ToHexString(aiKey.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(exchangeKey.EncryptedValue), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(aiKey.Id.ToString(), everything.Replace($"ai-key:{aiKey.Id}", string.Empty));

        // Where nothing is stored there is nothing to delete: the page leads back, and a form
        // that is sent a second time is answered as the first one was.
        Assert.Equal(KeysPage, (await client.OpenAsync(KeyBrowser.DeleteAiKeyPage("claude"))).Address);
        Assert.Equal(KeysPage, (await client.DeleteAiKeyAsync("claude")).Path);
        Assert.Equal(KeysPage, (await client.DeleteExchangeKeyAsync("demo")).Path);
        Assert.Equal(["grok"], StoredAiCredential.All(application.Database).Select(key => key.Provider));

        // The key is not brought back by anything but its user, who adds one anew, with a proof.
        Assert.Equal(ConfirmPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        await client.ConfirmIdentityAsync();
        Assert.Equal(KeysPage, (await client.StoreAiKeyAsync("claude", Keys.Claude)).Path);
        Assert.NotEqual(aiKey.Id, StoredAiCredential.Of(application.Database, user.Id, "claude").Id);
    }

    [Fact]
    [ProvesOwnershipOf("ai_credentials", "exchange_credentials")]
    public async Task The_keys_of_a_user_are_out_of_reach_of_another_user()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);

        // Grace has a key for two providers and for both environments.
        var grace = await application.RegisterVerifiedAsync(Grace);
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.ConfirmIdentityAsync();
        await gracesBrowser.StoreAiKeyAsync("claude", Keys.Of("grace", "claude"));
        await gracesBrowser.StoreAiKeyAsync("grok", Keys.Of("grace", "grok"));
        await gracesBrowser.StoreExchangeKeyAsync("demo", Keys.Of("grace", "demo"), Keys.Of("grace-sealed", "demo"));
        await gracesBrowser.StoreExchangeKeyAsync("live", Keys.Of("grace", "live"), Keys.Of("grace-sealed", "live"));

        var before = application.RowsOf(grace.Id);
        Assert.Equal(2, before["ai_credentials"].Count);
        Assert.Equal(2, before["exchange_credentials"].Count);

        // Ada sees none of them: every place is empty for her, on every page.
        clock.Set(Morning.AddMinutes(5));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        var list = await client.PageAsync(KeysPage);

        Assert.Equal(6, Regex.Count(list, Regex.Escape(NothingStored)));
        Assert.DoesNotContain("<code>", list);
        Assert.Contains("<h1>Add your Claude key</h1>", await client.PageAsync(KeyBrowser.AiKeyPage("claude")));
        Assert.Contains("<h1>Add your Binance keys for Live</h1>", await client.PageAsync(KeyBrowser.ExchangeKeyPage("live")));

        // Where she has no key there is nothing for her to delete, whoever else has one: the
        // page leads back, and a form that is posted all the same deletes nothing.
        Assert.Equal(KeysPage, (await client.OpenAsync(KeyBrowser.DeleteAiKeyPage("claude"))).Address);
        Assert.Equal(KeysPage, (await client.DeleteAiKeyAsync("claude")).Path);
        Assert.Equal(KeysPage, (await client.DeleteExchangeKeyAsync("live")).Path);

        // What she adds, replaces, and deletes is her own, in the same places.
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        await client.StoreAiKeyAsync("grok", Keys.Of("ada", "grok"));
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));
        await client.DeleteAiKeyAsync("grok");
        await client.DeleteExchangeKeyAsync("demo");

        Assert.Equal(["claude"], StoredAiCredential.All(application.Database).Where(key => key.UserId == ada.Id).Select(key => key.Provider));
        Assert.Equal(["Live"], StoredExchangeCredential.All(application.Database).Where(key => key.UserId == ada.Id).Select(key => key.Environment));

        // Her pages and her download show her keys, and nothing of Grace's.
        var pages = new[] { await client.PageAsync(KeysPage), await client.PageAsync("/account/export") };
        Assert.All(pages, page =>
        {
            Assert.Contains("Qr5T", page);
            Assert.DoesNotContain("grok", page.Replace("/account/keys/ai/grok", string.Empty));
            Assert.DoesNotContain(grace.Id.ToString(), page);
        });

        // And every row of Grace is as it was, to the last column.
        Assert.Equal(TextOf(before), TextOf(application.RowsOf(grace.Id)));
        Assert.Contains("<code>••••grok</code>", KeyBrowser.Read(await gracesBrowser.PageAsync(KeysPage)));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Theory]
    [InlineData("/account/keys")]
    [InlineData("/account/keys/ai/claude")]
    [InlineData("/account/keys/ai/claude/delete")]
    [InlineData("/account/keys/binance/demo")]
    [InlineData("/account/keys/binance/demo/delete")]
    public async Task A_visitor_who_opens_its_pages_is_sent_to_sign_in(string page)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    [Fact]
    public async Task A_visitor_who_posts_its_forms_is_sent_to_sign_in_and_changes_nothing()
    {
        using var application = new AccountsApplication();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);

        using var client = application.CreateClient();
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath));

        Assert.Equal(
            SessionAuthentication.SignInPath,
            (await client.PostAsync(KeyBrowser.AiKeyPage("gemini"), KeyBrowser.StoreForm, token, ("ApiKey", Keys.Claude))).Path);
        Assert.Equal(
            SessionAuthentication.SignInPath,
            (await client.PostAsync(KeyBrowser.DeleteAiKeyPage("claude"), KeyBrowser.DeleteForm, token)).Path);
        Assert.Equal(
            SessionAuthentication.SignInPath,
            (await client.PostAsync(KeyBrowser.DeleteExchangeKeyPage("demo"), KeyBrowser.DeleteForm, token)).Path);

        // Nor does a visitor have a provider asked about a key that is stored.
        Assert.Equal(SessionAuthentication.SignInPath, (await client.PostAsync(KeysPage, "check-ai-claude", token)).Path);
        Assert.Equal(SessionAuthentication.SignInPath, (await client.PostAsync(KeysPage, "check-binance-demo", token)).Path);

        Assert.Equal(["claude"], StoredAiCredential.All(application.Database).Select(key => key.Provider));
        Assert.Equal(["Demo"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));
        Assert.Empty(application.Providers.Asked);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_changes_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        var stored = application.RowsOf(user.Id);

        // As another site would send them, with the cookies of the user and without the token
        // of a page: each is refused, whatever it asks for.
        (string Path, string Form, (string Name, string Value)[] Fields)[] forms =
        [
            (KeyBrowser.AiKeyPage("claude"), KeyBrowser.StoreForm, [("ApiKey", Keys.AnotherClaude)]),
            (KeyBrowser.AiKeyPage("gemini"), KeyBrowser.StoreForm, [("ApiKey", Keys.AnotherClaude)]),
            (KeyBrowser.ExchangeKeyPage("demo"), KeyBrowser.StoreForm, [("ApiKey", Keys.Claude), ("SecretKey", Keys.AnotherClaude)]),
            (KeyBrowser.DeleteAiKeyPage("claude"), KeyBrowser.DeleteForm, []),
            (KeyBrowser.DeleteExchangeKeyPage("demo"), KeyBrowser.DeleteForm, []),

            // The forms of the list, which have a provider asked about a stored key again.
            (KeysPage, "check-ai-claude", []),
            (KeysPage, "check-binance-demo", []),
        ];

        foreach (var (path, form, fields) in forms)
        {
            using var answer = await client.SendFormAsync(path, form, "not-the-token", fields);

            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }

        Assert.Equal(
            string.Join('\n', stored["ai_credentials"].Concat(stored["exchange_credentials"])),
            string.Join('\n', application.RowsOf(user.Id)["ai_credentials"].Concat(application.RowsOf(user.Id)["exchange_credentials"])));
    }

    [Theory]
    [InlineData("/account/keys/ai/nobody")]
    [InlineData("/account/keys/ai/nobody/delete")]
    [InlineData("/account/keys/binance/paper")]
    [InlineData("/account/keys/binance/paper/delete")]
    [InlineData("/account/keys/binance/1")]
    public async Task A_provider_or_an_environment_that_does_not_exist_is_not_found(string page)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("name=\"ApiKey\"", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A form that is posted there is refused, and stores nothing. The page that says "not
        // found" has no such form, so the answer is that of a form which no page has.
        using var posted = await client.SendFormAsync(
            page,
            page.EndsWith("/delete", StringComparison.Ordinal) ? KeyBrowser.DeleteForm : KeyBrowser.StoreForm,
            Browser.AntiforgeryTokenIn(await client.PageAsync("/account")),
            ("ApiKey", Keys.Claude),
            ("SecretKey", Keys.AnotherClaude));

        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        Assert.Empty(StoredAiCredential.All(application.Database));
        Assert.Empty(StoredExchangeCredential.All(application.Database));
    }

    [Fact]
    [ShowsAuditOf(AuditAction.KeyAdded, AuditAction.KeyReplaced, AuditAction.KeyDeleted)]
    public async Task Adding_replacing_and_deleting_a_key_are_recorded_in_the_audit_trail_by_the_id_of_the_key()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        await client.StoreAiKeyAsync("claude", Keys.Claude);
        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude").Id;
        clock.Set(Morning.AddMinutes(1));
        await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        clock.Set(Morning.AddMinutes(2));
        await client.DeleteAiKeyAsync("claude");

        clock.Set(Morning.AddMinutes(3));
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Live").Id;
        clock.Set(Morning.AddMinutes(4));
        await client.StoreExchangeKeyAsync("live", Keys.Of("ada", "live"), Keys.Of("ada-sealed", "live"));
        clock.Set(Morning.AddMinutes(5));
        await client.DeleteExchangeKeyAsync("live");

        // What does not change a key is not an event: looking, a key that is not one, and
        // deleting where nothing is stored.
        await client.PageAsync(KeysPage);
        await client.StoreAiKeyAsync("gemini", "not a key");
        await client.DeleteAiKeyAsync("gemini");

        // One event for each change, in order, with the user, the key by the id of its row,
        // the time, and where the request came from.
        var events = StoredAuditEvent.All(application.Database)
            .Where(recorded => recorded.Action.StartsWith("Key", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                $"KeyAdded ai-key:{aiKey} {Morning:O}",
                $"KeyReplaced ai-key:{aiKey} {Morning.AddMinutes(1):O}",
                $"KeyDeleted ai-key:{aiKey} {Morning.AddMinutes(2):O}",
                $"KeyAdded exchange-key:{exchangeKey} {Morning.AddMinutes(3):O}",
                $"KeyReplaced exchange-key:{exchangeKey} {Morning.AddMinutes(4):O}",
                $"KeyDeleted exchange-key:{exchangeKey} {Morning.AddMinutes(5):O}",
            ],
            events.Select(recorded => $"{recorded.Action} {recorded.Target} {recorded.OccurredAt:O}"));
        Assert.All(events, recorded => Assert.Equal((user.Id, "203.0.113.7"), (recorded.UserId, recorded.Origin)));

        // The trail holds no key, and not even the end of one.
        var trail = string.Join('\n', application.RowsOf(user.Id)["audit_events"]);
        Assert.All(
            new[] { Keys.Claude, Keys.AnotherClaude, Keys.BinanceApi, Keys.BinanceSecret, "Wx7Z", "Qr5T", "Lm3N", "Zz9Y" },
            text => Assert.DoesNotContain(text, trail));

        // The log tells the same in its own words, without the names of the actions.
        Assert.DoesNotContain(application.Events(), written => written.ToString().Contains("KeyAdded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_download_names_each_key_by_its_last_four_characters_and_never_holds_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // Nothing is stored at first, and the sections are there all the same.
        var empty = System.Text.Json.JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement;
        Assert.Equal(0, empty.GetProperty("aiKeys").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("exchangeKeys").GetArrayLength());

        await client.StoreAiKeyAsync("grok", Keys.Of("ada", "grok"));
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        clock.Set(Morning.AddMinutes(2));
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);

        var aiKey = StoredAiCredential.Of(application.Database, user.Id, "claude");
        var exchangeKey = StoredExchangeCredential.Of(application.Database, user.Id, "Live");

        var text = await client.PageAsync("/account/export");
        var export = System.Text.Json.JsonDocument.Parse(text).RootElement;

        // Which keys there are, by provider, each with the id that the audit trail names it by.
        var aiKeys = export.GetProperty("aiKeys").EnumerateArray().ToList();
        Assert.All(
            aiKeys,
            key => Assert.Equal(
                ["id", "provider", "endsWith", "savedAt", "status", "validatedAt"], key.EnumerateObject().Select(member => member.Name)));
        Assert.Equal(
            [$"{aiKey.Id} claude Wx7Z {Morning:O}", $"{StoredAiCredential.Of(application.Database, user.Id, "grok").Id} grok grok {Morning:O}"],
            aiKeys.Select(key =>
                $"{key.GetProperty("id").GetGuid()} {key.GetProperty("provider").GetString()} " +
                $"{key.GetProperty("endsWith").GetString()} {key.GetProperty("savedAt").GetDateTimeOffset():O}"));

        var exchange = Assert.Single(export.GetProperty("exchangeKeys").EnumerateArray());
        Assert.Equal(
            ["id", "environment", "apiKeyEndsWith", "savedAt", "status", "validatedAt", "spotTrading", "futuresTrading", "ipRestricted"],
            exchange.EnumerateObject().Select(member => member.Name));
        Assert.Equal(exchangeKey.Id, exchange.GetProperty("id").GetGuid());
        Assert.Equal("Live", exchange.GetProperty("environment").GetString());
        Assert.Equal("Lm3N", exchange.GetProperty("apiKeyEndsWith").GetString());
        Assert.Equal(Morning.AddMinutes(2), exchange.GetProperty("savedAt").GetDateTimeOffset());

        // The events of the trail name the same ids.
        Assert.Contains(
            export.GetProperty("auditEvents").EnumerateArray(),
            recorded => recorded.GetProperty("target").GetString() == $"ai-key:{aiKey.Id}");

        // No key is in it, readable or encrypted, in any of the forms that bytes are written in,
        // and nothing of the secret key of Binance.
        Assert.All(
            new[] { Keys.Claude, Keys.BinanceApi, Keys.BinanceSecret }.SelectMany(Keys.FormsOf),
            form => Assert.DoesNotContain(form, text));
        Assert.All(new[] { aiKey.EncryptedValue, exchangeKey.EncryptedValue }, bytes =>
        {
            Assert.DoesNotContain(Convert.ToBase64String(bytes), text);
            Assert.DoesNotContain(Convert.ToHexString(bytes), text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain("Zz9Y", text);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stands for what a page does with a request whose account is deleted, on another browser,
    /// after the request was signed in: it deletes the account, and then asks as the pages do.
    /// </summary>
    private static async Task<bool> GoneMeanwhileAsync(HttpContext request)
    {
        if (!request.Request.Path.StartsWithSegments(GoneMeanwhile))
        {
            return false;
        }

        var services = request.RequestServices;
        var userId = SignedInUser.RequiredIdOf(request);

        await using (var context = await services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(1, await context.Set<User>().Where(user => user.Id == userId).ExecuteDeleteAsync());
        }

        var keys = services.GetRequiredService<IStoredKeys>();
        var state = await keys.StateAsync(request);
        var storing = await keys.StoreAiKeyAsync(request, new AiProviderCode("claude"), Keys.Claude);
        var storingExchange = await keys.StoreExchangeKeyAsync(request, TradingMode.Demo, Keys.BinanceApi, Keys.BinanceSecret);
        await keys.DeleteAiKeyAsync(request, new AiProviderCode("claude"));
        await keys.DeleteExchangeKeyAsync(request, TradingMode.Demo);

        await request.Response.WriteAsync(
            $"verified: {state.AddressVerified}, keys: {state.AiKeys.Count(slot => slot.Key is not null)}, " +
            $"storing: {storing.Outcome} and {storingExchange.Outcome}, deleting: done");

        return true;
    }
}
