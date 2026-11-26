using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// An Operator can disable or enable a user account (FR-OPS-4). A disabled account is signed out
/// everywhere at once, and is not signed in until it is enabled. The operator finds the account
/// by its email address, on a page that shows nothing else of its user, and each switch is
/// recorded in the audit trail with who made it and for which account (FR-OPS-5). What becomes
/// of the agents of the account is in <c>AgentAccountDependentsTests</c>.
/// </summary>
public class AccountDisablingTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";
    private const string Otto = "otto@example.com";
    private const string Tess = "tess@example.com";

    private const string FindPage = WebUiModule.OperatorAccountsPath;
    private const string FindForm = "find-account";
    private const string DisableForm = "disable-account";
    private const string EnableForm = "enable-account";

    private const string DisableButton = "<button type=\"submit\">Disable the account</button>";
    private const string EnableButton = "<button type=\"submit\">Enable the account</button>";

    private const string IsDisabled = "This account is disabled, so you cannot sign in.";
    private const string NotRight = "The email address or the password is not right.";

    private const string DisabledTemplate =
        "User {UserId} disabled the account of user {AccountId}: it is not signed in until it is enabled, and {SessionCount} of its sessions ended";

    private const string EnabledTemplate = "User {UserId} enabled the account of user {AccountId}: its user can sign in again";

    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.AccountDisabled)]
    public async Task An_operator_finds_an_account_by_its_address_and_disables_it_and_that_is_stored_recorded_and_shown()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);

        // The screen of an operator leads to the page that finds an account.
        Assert.Matches(
            $"<a href=\"{FindPage}\"[^>]*>Disable or enable an account</a>", await client.PageAsync(WebUiModule.OperatorStatusPath));

        // An address is found however it is typed, as an account is signed in to. The page of
        // the account names it by its id, so no address is in the address of a page.
        var (found, before) = await client.SubmitAsync(FindPage, FindForm, ("Email", "  ADA@Example.com "));

        Assert.Equal(WebUiModule.OperatorAccountPath(ada.Id), found);
        Assert.Contains("<h1>Account of a user</h1>", before);
        Assert.Contains($"The account of {Ada} is enabled.", before);
        Assert.Contains(DisableButton, before);
        Assert.DoesNotContain(EnableButton, before);
        Assert.Null(DisabledAt(application, ada.Id));

        clock.Set(Morning.AddMinutes(5));
        var (path, page) = await client.SubmitAsync(found, DisableForm);

        // She is back on the page, which says that the account is disabled, since when, and has
        // the button that enables it.
        Assert.Equal(found, path);
        Assert.Contains($"The account of {Ada} is disabled, since 26 November 2026, 09:05 UTC.", page);
        Assert.Contains(EnableButton, page);
        Assert.DoesNotContain(DisableButton, page);

        // It is a column of the account, which says since when and not by whom.
        Assert.Equal(Morning.AddMinutes(5), DisabledAt(application, ada.Id));
        Assert.Null(DisabledAt(application, olive.Id));

        // Who did it is an event of the audit trail, of the operator: who, what, to which
        // account, when, and from where (FR-OPS-5).
        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountDisabled");

        Assert.Equal(
            (olive.Id, $"user:{ada.Id}", Morning.AddMinutes(5), "203.0.113.7"),
            (recorded.UserId, recorded.Target, recorded.OccurredAt, recorded.Origin));

        // The log says it by the ids of both accounts, and holds no address.
        var said = Assert.Single(application.Events(), written => written.Template == DisabledTemplate);

        Assert.Equal((olive.Id.ToString(), ada.Id.ToString(), 0), (said.Text("UserId"), said.Text("AccountId"), said["SessionCount"].GetInt32()));
        Assert.DoesNotContain(Ada, application.LogText());
    }

    [Fact]
    [ShowsAuditOf(AuditAction.AccountEnabled)]
    public async Task An_operator_enables_the_account_again_and_its_user_signs_in()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        using var user = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);

        var account = WebUiModule.OperatorAccountPath(ada.Id);
        await client.SubmitAsync(account, DisableForm);

        Assert.Contains(IsDisabled, (await user.SignInAsync(Ada)).Page);

        clock.Set(Morning.AddMinutes(10));
        var (path, page) = await client.SubmitAsync(account, EnableForm);

        Assert.Equal(account, path);
        Assert.Contains($"The account of {Ada} is enabled.", page);
        Assert.Contains(DisableButton, page);
        Assert.Null(DisabledAt(application, ada.Id));

        // Both switches are in the trail, each as what it was, in the order they were made in.
        Assert.Equal(
            [
                (olive.Id, "AccountDisabled", $"user:{ada.Id}", Morning),
                (olive.Id, "AccountEnabled", $"user:{ada.Id}", Morning.AddMinutes(10)),
            ],
            StoredAuditEvent.All(application.Database)
                .Where(audited => audited.Action.StartsWith("Account", StringComparison.Ordinal))
                .Select(audited => (audited.UserId, audited.Action, audited.Target, audited.OccurredAt)));

        var said = Assert.Single(application.Events(), written => written.Template == EnabledTemplate);
        Assert.Equal((olive.Id.ToString(), ada.Id.ToString()), (said.Text("UserId"), said.Text("AccountId")));

        // Her password signs her in as before: nothing of the account was changed or deleted.
        Assert.Equal("/", (await user.SignInAsync(Ada)).Path);
        Assert.Contains("Sign out", await user.PageAsync("/account"));
    }

    [Fact]
    public async Task A_disabled_account_is_signed_out_on_every_browser_at_once_and_its_password_signs_nobody_in()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var anOperator = application.CreateClient();
        using var atHome = application.CreateClient();
        using var atWork = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var otto = await application.RegisterVerifiedAsync(Otto);
        await anOperator.SignInAsync(Olive);

        // She is signed in on two browsers, and another user on one.
        await atHome.SignInAsync(Ada);
        await atWork.SignInAsync(Ada);
        using var other = application.CreateClient();
        await other.SignInAsync(Otto);

        Assert.Equal(2, StoredSession.All(application.Database).Count(session => session.UserId == ada.Id));

        await anOperator.SubmitAsync(WebUiModule.OperatorAccountPath(ada.Id), DisableForm);

        // Her sessions ended with the change, and nobody else's.
        Assert.DoesNotContain(StoredSession.All(application.Database), session => session.UserId == ada.Id);
        Assert.Single(StoredSession.All(application.Database), session => session.UserId == otto.Id);
        Assert.Equal(2, Assert.Single(application.Events(), written => written.Template == DisabledTemplate)["SessionCount"].GetInt32());

        // So each of her browsers is sent to sign in with its next request.
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, "/account"), (await atHome.OpenAsync("/account")).Address);
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, "/account"), (await atWork.OpenAsync("/account")).Address);
        Assert.Contains("Sign out", await other.PageAsync("/account"));

        // Her password is right, and signs nobody in: she is told what keeps her out.
        var (path, refused) = await atHome.SignInAsync(Ada);

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains(IsDisabled, refused);
        Assert.DoesNotContain(NotRight, refused);
        Assert.DoesNotContain(StoredSession.All(application.Database), session => session.UserId == ada.Id);

        // Nobody else is: a wrong password is answered as for any account, and as for an
        // address that has none, so the answer tells nothing of the account.
        var wrong = (await atWork.SignInAsync(Ada, "not her password at all")).Page;

        Assert.Contains(NotRight, wrong);
        Assert.DoesNotContain(IsDisabled, wrong);

        // No sign-in is in the trail for it: no session started.
        Assert.Equal(2, StoredAuditEvent.All(application.Database).Count(audited => audited.UserId == ada.Id && audited.Action == "SignedIn"));
    }

    [Fact]
    public async Task A_disabled_account_with_two_factor_authentication_is_asked_for_no_code_and_a_code_that_was_asked_for_signs_nobody_in()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var anOperator = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var (tess, app, _) = await application.RegisterWithTwoFactorAsync(Tess, clock);
        await anOperator.SignInAsync(Olive);

        // She gave her password, and is asked for a code.
        Assert.Equal(SessionAuthentication.SignInCodePath, (await user.SignInAsync(Tess)).Path);
        Assert.Single(StoredPendingSignIn.All(application.Database), pending => pending.UserId == tess.Id);

        await anOperator.SubmitAsync(WebUiModule.OperatorAccountPath(tess.Id), DisableForm);

        // The sign-in that waited ended with her sessions, so the code that she gives now, which
        // is right, signs nobody in.
        Assert.Empty(StoredPendingSignIn.All(application.Database));

        await user.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));

        Assert.DoesNotContain(StoredSession.All(application.Database), session => session.UserId == tess.Id);
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, "/account"), (await user.OpenAsync("/account")).Address);

        // And her password alone is told what keeps her out: no sign-in waits for a code.
        var (path, refused) = await user.SignInAsync(Tess);

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains(IsDisabled, refused);
        Assert.Empty(StoredPendingSignIn.All(application.Database));
    }

    [Fact]
    public async Task A_session_of_an_account_that_was_disabled_in_the_database_by_hand_is_not_found()
    {
        using var application = Application(new TestClock(Morning));
        using var user = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await user.SignInAsync(Ada);

        Assert.Contains("Sign out", await user.PageAsync("/account"));

        // Whoever runs the database disables the account there, which ends no session.
        application.Database.Execute($"UPDATE users SET disabled_at = now() WHERE id = '{ada.Id}'");

        Assert.Single(StoredSession.All(application.Database), session => session.UserId == ada.Id);

        // The session is there, and no request is signed in with it.
        Assert.Equal(
            SessionAuthentication.PageWith(SessionAuthentication.SignInPath, "/account"), (await user.OpenAsync("/account")).Address);
        Assert.Contains(IsDisabled, (await user.SignInAsync(Ada)).Page);

        // Enabled there again, she signs in.
        application.Database.Execute($"UPDATE users SET disabled_at = NULL WHERE id = '{ada.Id}'");

        Assert.Equal("/", (await user.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task A_form_that_is_sent_twice_switches_and_records_nothing_more()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);

        var account = WebUiModule.OperatorAccountPath(ada.Id);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(account));

        // The same form, sent twice, as a second click does.
        await client.PostAsync(account, DisableForm, token);
        clock.Set(Morning.AddMinutes(1));
        var (path, again) = await client.PostAsync(account, DisableForm, token);

        // The second is answered as the first was, and found the account disabled: it is
        // disabled since the first, and one event says so.
        Assert.Equal(account, path);
        Assert.Contains($"The account of {Ada} is disabled, since 26 November 2026, 09:00 UTC.", again);
        Assert.Equal(Morning, DisabledAt(application, ada.Id));
        Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountDisabled");

        // And so for enabling it, also when the form is one of a page that showed it disabled.
        token = Browser.AntiforgeryTokenIn(again);
        await client.PostAsync(account, EnableForm, token);
        await client.PostAsync(account, EnableForm, token);

        Assert.Null(DisabledAt(application, ada.Id));
        Assert.Single(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountEnabled");
    }

    [Fact]
    public async Task The_account_of_an_operator_is_not_disabled_their_own_included()
    {
        using var application = Application(
            new TestClock(Morning), settings: file => file.WithJson("Application:Operators", $"[ \"{Olive}\", \"{Otto}\" ]"));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        var otto = await application.RegisterVerifiedAsync(Otto);
        await client.SignInAsync(Olive);

        foreach (var anOperator in new[] { olive, otto })
        {
            // The page of the account says why it has no button.
            var (found, page) = await client.SubmitAsync(FindPage, FindForm, ("Email", anOperator.Email));

            Assert.Equal(WebUiModule.OperatorAccountPath(anOperator.Id), found);
            Assert.Contains($"The account of {anOperator.Email} is enabled.", page);
            Assert.Contains("It is the account of an operator, and the account of an operator is not disabled.", page);
            Assert.DoesNotContain(DisableButton, page);
            Assert.DoesNotContain(EnableButton, page);

            // A form that is sent all the same disables nothing, and leads back to the page.
            var (path, _) = await client.PostAsync(found, DisableForm, Browser.AntiforgeryTokenIn(page));

            Assert.Equal(found, path);
            Assert.Null(DisabledAt(application, anOperator.Id));
        }

        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountDisabled");

        // She is still signed in, and still an operator.
        Assert.Contains("<h1>Operator status</h1>", await client.PageAsync(WebUiModule.OperatorStatusPath));
    }

    [Fact]
    public async Task An_address_that_has_no_account_is_said_so_and_an_account_that_is_gone_has_no_page()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        foreach (var address in new[] { "nobody@example.com", "no address" })
        {
            var (path, page) = await client.SubmitAsync(FindPage, FindForm, ("Email", address));

            Assert.Equal(FindPage, path);
            Assert.Contains("<p class=\"form-error\" role=\"alert\">No account has this email address.</p>", page);
            Assert.DoesNotContain(DisableButton, page);
        }

        // The page of an id that names no account is answered as an address without a page is.
        using var gone = await client.GetAsync(WebUiModule.OperatorAccountPath(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("<h1>Page not found</h1>", await gone.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_user_who_is_not_an_operator_reaches_neither_the_pages_nor_the_switch_and_a_visitor_is_sent_to_sign_in()
    {
        using var application = Application(new TestClock(Morning));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var otto = await application.RegisterVerifiedAsync(Otto);
        await user.SignInAsync(Otto);

        var account = WebUiModule.OperatorAccountPath(ada.Id);
        var token = Browser.AntiforgeryTokenIn(await user.PageAsync("/account"));

        // The pages are refused, and so is each form of them, with no word of an account (NFR-SEC-3).
        foreach (var page in new[] { FindPage, account })
        {
            using var refused = await user.GetAsync(page, TestContext.Current.CancellationToken);
            var said = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("<h1>Page not found</h1>", said);
            Assert.DoesNotContain(Ada, said);
        }

        using (var find = await user.SendFormAsync(FindPage, FindForm, token, ("Email", Ada)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, find.StatusCode);
        }

        using (var disable = await user.SendFormAsync(account, DisableForm, token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        }

        Assert.Null(DisabledAt(application, ada.Id));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountDisabled");
        Assert.Contains(
            application.Events(),
            written => written.Level == "Warning"
                && written.Template == "User {UserId} was refused {Path}, which is not for them"
                && written.Text("UserId") == otto.Id.ToString());

        // A visitor is sent to sign in, and a form that they post is not looked at.
        Assert.Equal(SessionAuthentication.PageWith(SessionAuthentication.SignInPath, FindPage), (await visitor.OpenAsync(FindPage)).Address);
        Assert.Equal(SessionAuthentication.PageWith(SessionAuthentication.SignInPath, account), (await visitor.OpenAsync(account)).Address);

        var (path, _) = await visitor.PostAsync(
            account, DisableForm, Browser.AntiforgeryTokenIn(await visitor.PageAsync(SessionAuthentication.SignInPath)));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Null(DisabledAt(application, ada.Id));
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_switches_nothing()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);

        // From a page of another site, which has no token of this one (NFR-SEC-5).
        using var answer = await client.SendFormAsync(WebUiModule.OperatorAccountPath(ada.Id), DisableForm, "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Null(DisabledAt(application, ada.Id));
    }

    [Fact]
    public async Task What_finds_and_switches_an_account_asks_again_whether_the_user_of_its_request_is_an_operator()
    {
        // Something that reaches the service past its pages, as nothing of the application does:
        // it finds the account of an address, and disables it, for whoever sent the request.
        const string direct = "/disables-an-account-directly";

        using var application = Application(new TestClock(Morning), host: host => host.HandleAtTheEnd(async request =>
        {
            if (!request.Request.Path.StartsWithSegments(direct))
            {
                return false;
            }

            var accounts = request.RequestServices.GetRequiredService<IAccountDisabling>();
            var found = await accounts.FindAsync(request, Ada);
            var byId = await accounts.FindAsync(request, Guid.Parse(request.Request.Query["account"]!));
            var outcome = await accounts.DisableAsync(request, Guid.Parse(request.Request.Query["account"]!));

            await request.Response.WriteAsync($"{(found is null ? "nothing" : "found")} {(byId is null ? "nothing" : "found")} {outcome}");

            return true;
        }));
        using var visitor = application.CreateClient();
        using var user = application.CreateClient();
        using var anOperator = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var otto = await application.RegisterVerifiedAsync(Otto);
        await application.RegisterVerifiedAsync(Olive);
        await user.SignInAsync(Otto);
        await anOperator.SignInAsync(Olive);

        var asked = $"{direct}?account={ada.Id}";

        // Nobody who is not an operator finds or disables an account, signed in or not (NFR-SEC-3).
        Assert.Equal("nothing nothing Refused", await visitor.PageAsync(asked));
        Assert.Equal("nothing nothing Refused", await user.PageAsync(asked));
        Assert.Null(DisabledAt(application, ada.Id));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Action == "AccountDisabled");

        // It says so in the log, by the account of whoever asked, where there is one.
        Assert.Contains(
            application.Events(),
            written => written.Level == "Warning"
                && written.Template == "User {UserId} was refused finding, disabling, or enabling an account, which is for an operator"
                && written.Text("UserId") == otto.Id.ToString());

        // An operator does both, and an account that there is none of is said as that.
        Assert.Equal("found found Done", await anOperator.PageAsync(asked));
        Assert.Equal(Morning, DisabledAt(application, ada.Id));
        Assert.Equal("found found AlreadySo", await anOperator.PageAsync(asked));
        Assert.Equal("found nothing NotFound", await anOperator.PageAsync($"{direct}?account={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task The_event_stays_with_the_operator_when_the_account_that_it_names_is_deleted()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);

        var account = WebUiModule.OperatorAccountPath(ada.Id);
        await client.SubmitAsync(account, DisableForm);
        await client.SubmitAsync(account, EnableForm);

        // The account that was disabled and enabled is deleted, with everything that hangs on it.
        application.Database.Execute($"DELETE FROM users WHERE id = '{ada.Id}'");

        // What the operator did is still recorded, under her own account. The event names the
        // account by its id, which names nobody now, and nothing that is stored holds its address.
        Assert.Equal(
            [(olive.Id, "AccountDisabled", $"user:{ada.Id}"), (olive.Id, "AccountEnabled", $"user:{ada.Id}")],
            StoredAuditEvent.All(application.Database)
                .Where(audited => audited.Action.StartsWith("Account", StringComparison.Ordinal))
                .Select(audited => (audited.UserId, audited.Action, audited.Target)));
        Assert.DoesNotContain(Ada, application.EverythingStored());

        // And its page is gone with it.
        using var gone = await client.GetAsync(account, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task The_pages_of_an_account_show_its_address_and_whether_it_is_disabled_and_nothing_else_of_its_user()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();

        // A user with stored keys and two-factor authentication: what an operator is never
        // shown (FR-OPS-5).
        var (tess, _, _) = await application.RegisterWithTwoFactorAsync(Tess, clock);
        await application.GiveKeysAsync(tess.Id, clock.GetUtcNow());

        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        var account = WebUiModule.OperatorAccountPath(tess.Id);
        var seen = new List<string>
        {
            await client.PageAsync(FindPage),
            await client.PageAsync(account),
            (await client.SubmitAsync(account, DisableForm)).Page,
            (await client.SubmitAsync(account, EnableForm)).Page,
        };

        // The page that finds an account names none. The page of an account names its address,
        // which the operator typed to find it. No page holds a key, masked or not, says that the
        // account has two-factor authentication, or says who switched it: not even the id of
        // the operator herself, whose address the bar of her own pages shows as it does anywhere.
        Assert.DoesNotContain(Tess, seen[0]);
        Assert.All(seen.Skip(1), page => Assert.Contains(Tess, page));
        Assert.All(seen, page =>
        {
            Assert.Contains("<h1", page);
            Assert.DoesNotContain("••••", page);
            Assert.DoesNotContain("wo-factor", page);
            Assert.DoesNotContain(olive.Id.ToString(), page);
        });
    }

    /// <summary>The application with one operator in its configuration file, unless the test names others.</summary>
    private static AccountsApplication Application(
        TestClock clock, Func<SettingsJson, SettingsJson>? settings = null, Action<IWebHostBuilder>? host = null) =>
        new(clock: clock, settings: settings ?? (file => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]")), host: host);

    /// <summary>Since when an account is disabled, as its row says, or null when it is not.</summary>
    private static DateTimeOffset? DisabledAt(AccountsApplication application, Guid userId)
    {
        using var connection = application.Database.Open();
        using var command = new Npgsql.NpgsqlCommand("SELECT disabled_at FROM users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);

        return command.ExecuteScalar() is DateTime at ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }
}
