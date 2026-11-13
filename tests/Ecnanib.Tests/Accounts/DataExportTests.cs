using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Which tables of users' rows are in the export of a user's data, in one place. This file is
/// the source of truth: <c>docs/accounts.md</c> explains the rule, and
/// <see cref="DataExportTests"/> enforces it. A table that is added later fails there until it
/// is listed here, as exported or with why it is not.
/// </summary>
internal static class ExportRules
{
    /// <summary>The tables whose rows are in the export, each with the section of the export that holds them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Exported = new Dictionary<string, string>
    {
        ["legal_acceptances"] = "legalAcceptances",
        ["sessions"] = "sessions",
        ["audit_events"] = "auditEvents",

        // Which keys are stored, each by its last four characters, and never a key. Credentials
        // hands them over, and its own tests say what the sections hold: see KeyPageTests.
        ["ai_credentials"] = "aiKeys",
        ["exchange_credentials"] = "exchangeKeys",
    };

    /// <summary>The tables whose rows are not in the export, each with why.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotExported = new Dictionary<string, string>
    {
        ["pending_sign_ins"] =
            "A sign-in that waits for a code, for ten minutes at most. Its row holds the hash of a token and a time, " +
            "and nothing about the user.",
        ["two_factor_recovery_codes"] =
            "The hashes of secrets, which tell their user nothing. The export says how many codes are left.",
    };
}

/// <summary>
/// A user can export their data (NFR-SEC-9): a download of what is stored about them. It holds
/// no secret (FR-KEY-8), and nothing of another user (NFR-SEC-3).
/// </summary>
public class DataExportTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Export = WebUiModule.ExportPath;

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_download_holds_what_is_stored_about_the_user()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        var (ada, _, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        var signedInAt = clock.GetUtcNow();
        await client.SignInAsync(Ada);
        Assert.Equal("/", (await client.EnterCodeAsync(recoveryCodes[0])).Path);

        clock.Set(Morning.AddMinutes(5));
        using var answer = await client.GetAsync(Export, TestContext.Current.CancellationToken);
        var export = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(
            ["exportedAt", "account", "legalAcceptances", "sessions", "auditEvents", "aiKeys", "exchangeKeys"], NamesIn(export));
        Assert.Equal(Morning.AddMinutes(5), export.GetProperty("exportedAt").GetDateTimeOffset());

        // The account: the address, and when it was registered and verified. The password is
        // not in it, nor is its hash.
        var account = export.GetProperty("account");
        Assert.Equal(["id", "email", "registeredAt", "emailVerifiedAt", "twoFactor"], NamesIn(account));
        Assert.Equal(ada.Id, account.GetProperty("id").GetGuid());
        Assert.Equal(Ada, account.GetProperty("email").GetString());
        Assert.Equal(Morning, account.GetProperty("registeredAt").GetDateTimeOffset());
        Assert.Equal(Morning, account.GetProperty("emailVerifiedAt").GetDateTimeOffset());

        // Where two-factor authentication stands, without its key and its codes.
        var twoFactor = account.GetProperty("twoFactor");
        Assert.Equal(["on", "since", "recoveryCodesLeft"], NamesIn(twoFactor));
        Assert.True(twoFactor.GetProperty("on").GetBoolean());
        Assert.Equal(Morning, twoFactor.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(TwoFactorRules.RecoveryCodeCount - 1, twoFactor.GetProperty("recoveryCodesLeft").GetInt32());

        // What was accepted, by the names of the documents and not by their numbers.
        var acceptances = export.GetProperty("legalAcceptances").EnumerateArray().ToList();
        Assert.All(acceptances, acceptance =>
        {
            Assert.Equal(["document", "version", "acceptedAt"], NamesIn(acceptance));
            Assert.Equal(Morning, acceptance.GetProperty("acceptedAt").GetDateTimeOffset());
        });
        Assert.Equal(
            ["PrivacyPolicy 0.1", "RiskDisclosure 0.1", "TermsOfService 0.1"],
            acceptances.Select(acceptance => $"{acceptance.GetProperty("document").GetString()} {acceptance.GetProperty("version").GetString()}"));

        // The browser that is signed in, without its token. This request counted as activity.
        var session = Assert.Single(export.GetProperty("sessions").EnumerateArray());
        Assert.Equal(["createdAt", "lastActiveAt"], NamesIn(session));
        Assert.Equal(signedInAt, session.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(Morning.AddMinutes(5), session.GetProperty("lastActiveAt").GetDateTimeOffset());

        // The audit trail of the account, in the order it was recorded, with where each action
        // came from: the sign-in that turned two-factor authentication on, and the recovery
        // code that was spent on the sign-in after it.
        var events = export.GetProperty("auditEvents").EnumerateArray().ToList();
        Assert.All(events, recorded => Assert.Equal(["action", "target", "occurredAt", "origin"], NamesIn(recorded)));
        Assert.Equal(
            [
                $"SignedIn user:{ada.Id} {Morning:O} 203.0.113.7",
                $"TwoFactorTurnedOn user:{ada.Id} {Morning:O} 203.0.113.7",
                $"TwoFactorRecoveryCodeUsed user:{ada.Id} {signedInAt:O} 203.0.113.7",
                $"SignedIn user:{ada.Id} {signedInAt:O} 203.0.113.7",
            ],
            events.Select(recorded =>
                $"{recorded.GetProperty("action").GetString()} {recorded.GetProperty("target").GetString()} " +
                $"{recorded.GetProperty("occurredAt").GetDateTimeOffset():O} {recorded.GetProperty("origin").GetString()}"));
    }

    [Fact]
    public async Task The_download_is_a_file_to_keep_that_a_person_can_read_and_that_no_browser_stores()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.GetAsync(Export, TestContext.Current.CancellationToken);
        var text = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", answer.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("account-data-2026-11-12.json", answer.Content.Headers.ContentDisposition?.FileName);
        Assert.True(answer.Headers.CacheControl?.NoStore, "What is stored about a person is not to be kept by a browser or a proxy.");

        // One thing to a line, as a person reads it, with nothing left out for not being set.
        Assert.Contains($"{Environment.NewLine}  \"account\": {{{Environment.NewLine}", text);
        Assert.Contains("\"since\": null", text);

        // The account page leads to it, as a download: the browser asks for the file once and
        // does not try to show it as a page.
        Assert.Contains("<a href=\"/account/export\" download>Download your data</a>", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task The_download_holds_no_secret()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (ada, app, recoveryCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        using var phone = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        // Everything that the account can have of secrets is there: a session, a sign-in that
        // waits for a code, a reset link that is out, the key, the recovery codes, and a key of
        // an AI provider and of Binance.
        await phone.SignInAsync(Ada);
        await application.RequestResetAsync(Ada);
        var resetToken = AccountsApplication.TokenIn(application.Email.Sent[^1]);
        await application.GiveKeysAsync(ada.Id, clock.GetUtcNow());

        var user = StoredUser.Of(application.Database, Ada)!;
        var twoFactor = StoredTwoFactor.Of(application.Database, Ada);
        byte[][] stored =
        [
            twoFactor.Secret!,
            app.Key,
            user.ResetTokenHash!,
            .. StoredSession.All(application.Database).Select(session => session.TokenHash),
            .. StoredPendingSignIn.All(application.Database).Select(pending => pending.TokenHash),
            .. StoredRecoveryCode.All(application.Database).Select(code => code.CodeHash),
            .. StoredAiCredential.All(application.Database).Select(key => key.EncryptedValue),
            .. StoredExchangeCredential.All(application.Database).Select(key => key.EncryptedValue),
        ];
        Assert.Equal(4 + 1 + TwoFactorRules.RecoveryCodeCount + 2, stored.Length);

        var text = await client.PageAsync(Export);

        // Neither the hash of the password nor anything else that would help someone in, in any
        // of the forms that bytes are written in.
        Assert.DoesNotContain(user.PasswordHash, text);
        Assert.DoesNotContain("argon2", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(stored, bytes =>
        {
            Assert.DoesNotContain(Convert.ToBase64String(bytes), text);
            Assert.DoesNotContain(Base64Url.EncodeToString(bytes), text);
            Assert.DoesNotContain(Convert.ToHexString(bytes), text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(app.KeyText, text);
        Assert.DoesNotContain(resetToken, text);
        Assert.All(recoveryCodes, code => Assert.DoesNotContain(code, text));
        Assert.All(recoveryCodes, code => Assert.DoesNotContain(code.Replace("-", string.Empty), text));

        // Nor the name of anything of that kind: what the export holds is what the other test lists.
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [ProvesOwnershipOf(
        "ai_credentials", "audit_events", "exchange_credentials", "legal_acceptances", "sessions", "two_factor_recovery_codes")]
    public async Task The_download_holds_nothing_of_another_user()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));

        // Grace has more of everything than Ada: two sessions, two events, recovery codes, and keys.
        var (grace, gracesApp, gracesCodes) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        using var gracesDesk = application.CreateClient();
        using var gracesPhone = application.CreateClient();
        await gracesDesk.SignInAsync(Grace);
        await gracesDesk.EnterCodeAsync(gracesApp.CodeAt(clock.GetUtcNow()));
        await gracesPhone.SignInAsync(Grace);
        await gracesPhone.EnterCodeAsync(gracesCodes[0]);
        await application.GiveKeysAsync(grace.Id, clock.GetUtcNow());
        Assert.All(
            ExportRules.Exported.Keys,
            table => Assert.True(application.RowsOf(grace.Id)[table].Count > 0, $"Grace has no row in {table}, so nothing shows that it stays out."));

        clock.Set(Morning.AddHours(1));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var text = await client.PageAsync(Export);
        var export = JsonDocument.Parse(text).RootElement;

        // What it holds is Ada's: her account, her one session, and the one event of hers that
        // the audit trail has, which is the sign-in that led here.
        Assert.Equal(ada.Id, export.GetProperty("account").GetProperty("id").GetGuid());
        Assert.Equal(0, export.GetProperty("account").GetProperty("twoFactor").GetProperty("recoveryCodesLeft").GetInt32());
        Assert.Equal(LegalDocuments.All.Count, export.GetProperty("legalAcceptances").GetArrayLength());
        Assert.Equal(1, export.GetProperty("sessions").GetArrayLength());

        var recorded = Assert.Single(export.GetProperty("auditEvents").EnumerateArray());
        Assert.Equal("SignedIn", recorded.GetProperty("action").GetString());
        Assert.Equal($"user:{ada.Id}", recorded.GetProperty("target").GetString());

        // She has stored no key, so the export names none.
        Assert.Equal(0, export.GetProperty("aiKeys").GetArrayLength());
        Assert.Equal(0, export.GetProperty("exchangeKeys").GetArrayLength());

        // And nothing in it is Grace's: not her address, not her id, and not a time of hers.
        Assert.DoesNotContain("grace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(grace.Id.ToString(), text);
        Assert.DoesNotContain("T09:", text);
    }

    [Fact]
    public async Task Every_table_of_users_rows_is_in_the_export_or_says_why_it_is_not()
    {
        using var model = ModelOnly.Context();
        var owned = OwnershipRules.UserOwnedTables(model.Model);
        var decided = ExportRules.Exported.Keys.Concat(ExportRules.NotExported.Keys).ToList();

        var undecided = owned.Except(decided).ToList();
        Assert.True(
            undecided.Count == 0,
            "These tables hold rows of users, and nothing says whether a user's export holds them (NFR-SEC-9). Add them to " +
            $"the export, or list them in {nameof(ExportRules)}.{nameof(ExportRules.NotExported)} with why they are left out. " +
            $"See docs/accounts.md: {string.Join(", ", undecided)}");

        Assert.Empty(decided.Except(owned));
        Assert.Empty(ExportRules.Exported.Keys.Intersect(ExportRules.NotExported.Keys));

        // What is listed as exported is: a user with a row in each table finds one in the
        // section that the list names.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (ada, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        await application.GiveKeysAsync(ada.Id, clock.GetUtcNow());

        var rows = application.RowsOf(ada.Id);
        var export = JsonDocument.Parse(await client.PageAsync(Export)).RootElement;

        Assert.All(ExportRules.Exported, exported =>
        {
            Assert.True(rows[exported.Key].Count > 0, $"Ada has no row in {exported.Key}, so nothing shows that the export holds it.");
            Assert.Equal(rows[exported.Key].Count, export.GetProperty(exported.Value).GetArrayLength());
        });
    }

    [Fact]
    public async Task The_log_says_who_exported_their_data_by_id_and_holds_nothing_of_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.Contains("203.0.113.7", await client.PageAsync(Export));

        var exported = Assert.Single(application.Events(), written => written.Template == "User {UserId} exported their data");
        Assert.Equal("Ecnanib.Accounts.DataExport", exported.Area);
        Assert.Equal(user.Id.ToString(), exported.Text("UserId"));

        var log = application.LogText();
        Assert.DoesNotContain("example.com", log);
        Assert.DoesNotContain("203.0.113.7", log);
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in_and_is_given_nothing()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var (asked, page) = await visitor.OpenAsync(Export);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount%2Fexport", asked);
        Assert.DoesNotContain(Ada, page);
    }

    /// <summary>The names of the members of a JSON object, in the order they are written in.</summary>
    private static IEnumerable<string> NamesIn(JsonElement json) => json.EnumerateObject().Select(member => member.Name);
}
