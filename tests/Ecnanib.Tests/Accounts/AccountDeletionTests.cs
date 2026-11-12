using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user can delete their account (FR-ACC-9, NFR-SEC-9). Deleting it removes every row that
/// belongs to the user, at once. It is done only for a user who has just proved again who they
/// are (FR-ACC-4), and only while every agent of theirs is stopped. Agents are not built yet,
/// so a stand-in of the tests answers for them, as Agents will.
/// </summary>
public class AccountDeletionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string DeletePage = WebUiModule.DeleteAccountPath;
    private const string DeletedPage = WebUiModule.AccountDeletedPath;
    private const string ConfirmPage = SessionAuthentication.ReauthenticationPath;
    private const string ConfirmFirst = $"{ConfirmPage}?returnUrl=%2Faccount%2Fdelete";

    private const string Button = "Delete my account";
    private const string AgentsStandInTheWay = "One of your agents is not stopped.";

    private const string DeletedTemplate = "User {UserId} deleted their account, which ended {SessionCount} sessions";
    private const string AskedTemplate = "User {UserId} is asked to confirm who they are before {Action}";

    private const string GoneMeanwhile = "/gone-meanwhile";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Deleting_an_account_removes_every_row_of_its_user_at_once_and_no_row_of_another()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));

        // Grace has a row in every table that holds rows of users: what she accepted, her
        // recovery codes, the event of turning two-factor authentication on, a session on her
        // desk, and a sign-in on her phone that waits for its code.
        var (grace, gracesApp, _) = await application.RegisterWithTwoFactorAsync(Grace, clock);
        using var gracesDesk = application.CreateClient();
        using var gracesPhone = application.CreateClient();
        await gracesDesk.SignInAsync(Grace);
        Assert.Equal("/", (await gracesDesk.EnterCodeAsync(gracesApp.CodeAt(clock.GetUtcNow()))).Path);
        await gracesPhone.SignInAsync(Grace);

        // So has Ada, who is signed in on two browsers.
        var (ada, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        using var desk = application.CreateClient();
        using var tablet = application.CreateClient();
        using var phone = application.CreateClient();
        await desk.SignInAsync(Ada);
        Assert.Equal("/", (await desk.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await tablet.SignInAsync(Ada);
        Assert.Equal("/", (await tablet.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
        await phone.SignInAsync(Ada);

        // The tables are those of the model, so a table that is added later is among them. It
        // fails here until this test gives both users a row in it, and so shows that deleting
        // an account deletes from it too.
        Assert.All(application.RowsOf(ada.Id), table => Assert.True(
            table.Value.Count > 0, $"Ada has no row in {table.Key}, so nothing shows that deleting her account deletes from it."));
        var gracesRows = application.RowsOf(grace.Id);
        Assert.All(gracesRows, table => Assert.True(
            table.Value.Count > 0, $"Grace has no row in {table.Key}, so nothing is compared there."));
        var gracesAccount = AccountOf(application, grace.Id);

        // Ada proves who she is, with her password and a code, and deletes her account.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await desk.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()));
        var (path, page) = await desk.DeleteAccountAsync();

        Assert.Equal(DeletedPage, path);
        Assert.Contains("<h1>Your account was deleted</h1>", page);

        // Nothing of hers is left: not the account, not a row that belonged to it, and nothing
        // anywhere in the database that names her address or her id.
        Assert.Null(StoredUser.Of(application.Database, Ada));
        Assert.All(application.RowsOf(ada.Id), table => Assert.Empty(table.Value));

        var stored = application.EverythingStored();
        Assert.DoesNotContain(Ada, stored);
        Assert.DoesNotContain(ada.Id.ToString(), stored);

        // Nobody is signed in to it any more, on the browser that deleted it or on the other.
        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount", (await desk.OpenAsync("/account")).Address);
        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount", (await tablet.OpenAsync("/account")).Address);
        Assert.DoesNotContain("Sign out", await desk.PageAsync("/"));

        // And nothing of Grace's was touched: her account and every row of hers are as they
        // were, to the last column.
        Assert.Equal(gracesAccount, AccountOf(application, grace.Id));
        Assert.Equal(TextOf(gracesRows), TextOf(application.RowsOf(grace.Id)));
        Assert.Contains("You are signed in", await gracesDesk.PageAsync("/account"));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Fact]
    public async Task An_account_is_not_deleted_until_its_user_has_proved_again_who_they_are()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // Being signed in is not enough to be shown the page: the browser is sent to the page
        // that takes the proof.
        var (asked, page) = await client.OpenAsync(DeletePage);

        Assert.Equal(ConfirmFirst, asked);
        Assert.Contains("<h1>Confirm that it is you</h1>", page);
        Assert.DoesNotContain(Button, page);

        // The form is posted all the same, as something that is not a page of the application
        // can, with a token against request forgery of the user's own. The check is in the
        // action: nothing is deleted, and the browser is sent to the page that takes the proof.
        var (path, _) = await client.PostAsync(
            DeletePage, "delete-account", Browser.AntiforgeryTokenIn(await client.PageAsync("/account")));

        Assert.Equal(ConfirmPage, path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
        Assert.Single(StoredSession.All(application.Database));

        // The log says what was held back, both times.
        var held = application.Events().Where(written => written.Template == AskedTemplate).ToList();
        Assert.Equal(2, held.Count);
        Assert.All(held, written =>
        {
            Assert.Equal(user.Id.ToString(), written.Text("UserId"));
            Assert.Equal(nameof(SensitiveAction.DeleteAccount), written.Text("Action"));
        });

        // The proof leads back to the page, which is shown now. Seeing it deletes nothing.
        var (back, shown) = await client.ConfirmIdentityAsync(page: asked);

        Assert.Equal(DeletePage, back);
        Assert.Contains(Button, shown);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));

        // The button does.
        Assert.Equal(DeletedPage, (await client.DeleteAccountAsync()).Path);
        Assert.Null(StoredUser.Of(application.Database, Ada));
    }

    [Fact]
    public async Task The_proof_is_one_of_the_browser_that_deletes_and_not_older_than_ten_minutes()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await desk.SignInAsync(Ada);
        await phone.SignInAsync(Ada);
        await desk.ConfirmIdentityAsync();

        // Another browser of the same user gave no proof, and is asked itself.
        Assert.Equal(ConfirmFirst, (await phone.OpenAsync(DeletePage)).Address);
        Assert.Equal(ConfirmPage, (await phone.DeleteAccountAsync()).Path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));

        // The page is shown while the proof holds. The button is pressed when it no longer
        // does: the answer is read from the database at that moment, and nothing is deleted.
        clock.Set(Morning + ReauthenticationRules.HoldsFor - TimeSpan.FromSeconds(1));
        var page = await desk.PageAsync(DeletePage);
        Assert.Contains(Button, page);

        clock.Set(Morning + ReauthenticationRules.HoldsFor);
        var (path, _) = await desk.PostAsync(DeletePage, "delete-account", Browser.AntiforgeryTokenIn(page));

        Assert.Equal(ConfirmPage, path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
        Assert.Equal(2, StoredSession.All(application.Database).Count);
    }

    [Fact]
    public async Task An_account_is_not_deleted_while_an_agent_of_its_user_is_not_stopped()
    {
        var agents = new StandInAgents { NotStopped = true };
        using var application = new AccountsApplication(
            host: host => host.ConfigureServices(services => services.AddSingleton<IAgentActivity>(agents)));
        agents.Database = application.Database;
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The page says what stands in the way, and offers no button.
        var page = await client.PageAsync(DeletePage);

        Assert.Contains(AgentsStandInTheWay, page);
        Assert.DoesNotContain(Button, page);

        // The form is posted all the same. Nothing is deleted, and the answer says why.
        var (path, answer) = await client.PostAsync(DeletePage, "delete-account", Browser.AntiforgeryTokenIn(page));

        Assert.Equal(DeletePage, path);
        Assert.Contains(AgentsStandInTheWay, answer);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
        Assert.Single(StoredSession.All(application.Database));
        Assert.Contains("You are signed in", await client.PageAsync("/account"));

        var refused = Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} did not delete their account: an agent of theirs is not stopped");
        Assert.Equal(user.Id.ToString(), refused.Text("UserId"));

        // It was asked about this user, inside a transaction both times: whoever answers locks
        // the rows it reads. For the deletion itself, that transaction held the lock on the
        // account's row by then, so that no row could be added to the account meanwhile.
        Assert.Equal(
            [
                new Question(user.Id, InTransaction: true, AccountLocked: false),
                new Question(user.Id, InTransaction: true, AccountLocked: true),
            ],
            agents.Asked);

        // Once every agent is stopped, the account is deleted.
        agents.NotStopped = false;
        Assert.Contains(Button, await client.PageAsync(DeletePage));
        Assert.Equal(DeletedPage, (await client.DeleteAccountAsync()).Path);
        Assert.Null(StoredUser.Of(application.Database, Ada));
    }

    [Fact]
    public void Once_agents_are_stored_the_component_that_keeps_them_says_whether_they_are_stopped()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Agents are not built yet, and nothing answers for them. As soon as the component has
        // a table of its own, it must: Accounts cannot look at agents itself.
        var storesRows = model.Model.GetEntityTypes().Any(entity => IsOfAgents(entity.ClrType));
        var answers = application.Services.GetServices<IAgentActivity>().Any(activity => IsOfAgents(activity.GetType()));

        Assert.True(
            answers || !storesRows,
            "Agents stores rows, and nothing of it says whether the agents of a user are stopped. An account must not be " +
            $"deleted while one is not (FR-ACC-9). Implement {nameof(IAgentActivity)} in Ecnanib.Agents and register it in " +
            "AddAgents(). See docs/accounts.md.");

        static bool IsOfAgents(Type type) =>
            type.Namespace is { } area && (area == "Ecnanib.Agents" || area.StartsWith("Ecnanib.Agents.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_address_of_a_deleted_account_has_no_account_and_can_register_again_as_a_new_one()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        var (first, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await client.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()));
        Assert.Equal(DeletedPage, (await client.DeleteAccountAsync()).Path);

        // The password signs in to nothing, and the answer is the one for an address without an account.
        var (path, page) = await client.SignInAsync(Ada);

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("The email address or the password is not right.", page);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Empty(StoredPendingSignIn.All(application.Database));

        // A reset link is not sent either: the address has no account.
        Assert.Equal(PasswordResetRequestOutcome.Accepted, await application.RequestResetAsync(Ada));
        Assert.Equal("No account at Ecnanib Test with this address", application.Email.Sent[^1].Subject);

        // Registering it again makes a new account, as for an address that was never here:
        // the link takes no password, and nothing of the earlier account comes back.
        clock.Set(Morning.AddDays(1));
        var second = await application.RegisterVerifiedAsync(Ada);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Morning.AddDays(1), second.RegisteredAt);
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.Empty(StoredAuditEvent.All(application.Database));
        Assert.Equal("/", (await client.SignInAsync(Ada)).Path);
    }

    [Fact]
    public async Task An_account_whose_address_was_never_verified_is_deleted_as_any_other()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        Assert.Equal(DeletedPage, (await client.DeleteAccountAsync()).Path);

        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));

        // The link that was sent to the address verifies nothing any more.
        Assert.False(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[0])));
    }

    [Fact]
    public async Task A_last_message_tells_the_address_that_the_account_was_deleted()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        var sentBefore = application.Email.Sent.Count;

        await client.DeleteAccountAsync();

        var notice = Assert.Single(application.Email.Sent.Skip(sentBefore));
        Assert.Equal(Ada, notice.To);
        Assert.Equal("Your account at Ecnanib Test was deleted", notice.Subject);
        Assert.Contains("was deleted, with everything that belonged to it", notice.TextBody);
        Assert.Contains("This is the last message to this email address", notice.TextBody);
    }

    [Fact]
    public async Task When_the_last_message_cannot_be_sent_the_account_is_deleted_all_the_same()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        Assert.Equal(DeletedPage, (await client.DeleteAccountAsync()).Path);

        Assert.Null(StoredUser.Of(application.Database, Ada));
        var failure = Assert.Single(application.Events(), written => written.Level == "Error");
        Assert.Equal("Ecnanib.Accounts.AccountDeletion", failure.Area);
        Assert.Equal("The notice of a deleted account could not be sent", failure.Template);
        Assert.Contains("The mail server is away.", failure.Exception);
        Assert.DoesNotContain("example.com", application.LogText());
    }

    [Fact]
    public async Task The_log_names_the_account_by_its_id_and_the_audit_trail_keeps_nothing_of_it()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.From("203.0.113.7"));
        using var desk = application.CreateClient();
        using var phone = application.CreateClient();
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        await desk.SignInAsync(Ada);
        await desk.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await phone.SignInAsync(Ada);
        await phone.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await desk.ConfirmIdentityAsync(code: app.CodeAt(clock.GetUtcNow()));
        Assert.NotEmpty(StoredAuditEvent.All(application.Database));

        await desk.DeleteAccountAsync();

        var deleted = Assert.Single(application.Events(), written => written.Template == DeletedTemplate);
        Assert.Equal("Ecnanib.Accounts.AccountDeletion", deleted.Area);
        Assert.Equal(user.Id.ToString(), deleted.Text("UserId"));
        Assert.Equal(2, deleted["SessionCount"].GetInt32());

        // The address is personal data, and so is where the user was: neither is in the log.
        var log = application.LogText();
        Assert.DoesNotContain("example.com", log);
        Assert.DoesNotContain("203.0.113.7", log);

        // The events of an account go with it, so the deletion is not among them: the audit
        // trail keeps nothing of an account that is gone.
        Assert.Empty(StoredAuditEvent.All(application.Database));
    }

    [Fact]
    public async Task The_browser_that_deleted_the_account_is_sent_on_without_its_session_cookie()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClientThatStays();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        var page = await client.PageAsync(DeletePage);

        using var answer = await client.SendFormAsync(DeletePage, "delete-account", Browser.AntiforgeryTokenIn(page));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal(DeletedPage, new Uri(client.BaseAddress!, answer.Headers.Location!).AbsolutePath);

        // The session cookie is taken from the browser, and no other cookie is touched.
        Assert.Equal(
            ["__Host-ecnanib-session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=lax; httponly"],
            Browser.CookiesSetBy(answer));

        // The page it is sent to is one for visitors.
        Assert.Contains("<h1>Your account was deleted</h1>", await client.PageAsync(DeletedPage));
    }

    [Fact]
    public async Task A_request_that_is_under_way_when_its_account_goes_is_answered_and_does_not_fail()
    {
        using var application = new AccountsApplication(host: host => host.HandleAtTheEnd(GoneMeanwhileAsync));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // The request is signed in, and its account is deleted before it asks for anything, as
        // from another browser of the same user at the same moment.
        var answer = await client.PageAsync(GoneMeanwhile);

        // An account that does not exist has nothing on and is not verified, has nothing to
        // export, and is deleted: which is what a form that was sent twice asked for.
        Assert.Equal("two-factor: off, turning it on: AddressNotVerified, export: nothing, deleting: Deleted", answer);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.DoesNotContain(application.Events(), written => written.Level is "Error" or "Fatal");
    }

    [Fact]
    public async Task The_account_page_leads_to_the_page_which_says_what_goes_and_offers_the_download_first()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        Assert.Contains("<a href=\"/account/delete\">Delete your account</a>", await client.PageAsync("/account"));

        await client.ConfirmIdentityAsync();
        var page = await client.PageAsync(DeletePage);

        Assert.Contains("<h1>Delete your account</h1>", page);
        Assert.Contains("It cannot be undone.", page);
        Assert.Contains("<a href=\"/account/export\" download>download your data</a>", page);
        Assert.Contains("<a href=\"/account\">Keep my account</a>", page);
        Assert.DoesNotContain(AgentsStandInTheWay, page);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_deletes_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // From a page of another site, which has no token of this one.
        using var answer = await client.SendFormAsync(DeletePage, "delete-account", "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
        Assert.Single(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in_and_deletes_nothing()
    {
        using var application = new AccountsApplication();
        using var visitor = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        Assert.Equal(
            $"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount%2Fdelete", (await visitor.OpenAsync(DeletePage)).Address);

        // A form that a visitor posts is not looked at: they are sent to sign in.
        var (path, _) = await visitor.PostAsync(
            DeletePage, "delete-account", Browser.AntiforgeryTokenIn(await visitor.PageAsync(SessionAuthentication.SignInPath)));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.NotNull(StoredUser.Of(application.Database, Ada));
    }

    /// <summary>The row of an account, as the database would hand it to whoever reads it.</summary>
    private static string AccountOf(AccountsApplication application, Guid userId) =>
        Assert.Single(application.Database.Column($"SELECT row_to_json(stored)::text FROM users stored WHERE id = '{userId}'"));

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

        var twoFactor = services.GetRequiredService<ITwoFactor>();
        var state = await twoFactor.StateAsync(request);
        var turningOn = await twoFactor.TurnOnAsync(request, string.Empty, string.Empty, string.Empty);
        var export = await services.GetRequiredService<IDataExport>().ExportAsync(request);
        var deleting = await services.GetRequiredService<IAccountDeletion>().DeleteAsync(request);

        await request.Response.WriteAsync(
            $"two-factor: {(state.IsOn ? "on" : "off")}, turning it on: {turningOn.Outcome}, " +
            $"export: {(export is null ? "nothing" : "something")}, deleting: {deleting}");

        return true;
    }

    /// <summary>One question to <see cref="StandInAgents"/>, with how it was asked.</summary>
    /// <param name="UserId">The user whose agents were asked about.</param>
    /// <param name="InTransaction">Whether the context it was asked with holds a transaction.</param>
    /// <param name="AccountLocked">Whether the row of the account was locked by then, so that no other session could lock it.</param>
    private sealed record Question(Guid UserId, bool InTransaction, bool AccountLocked);

    /// <summary>
    /// Stands for Agents, which is not built yet: it says whether a user has an agent that is
    /// not stopped, as a test tells it to, and keeps how it was asked.
    /// </summary>
    private sealed class StandInAgents : IAgentActivity
    {
        private readonly ConcurrentQueue<Question> _asked = new();

        /// <summary>What it answers.</summary>
        public bool NotStopped { get; set; }

        /// <summary>The database of the application, which it looks at with a session of its own.</summary>
        public TestDatabase? Database { get; set; }

        /// <summary>The questions it was asked, in order.</summary>
        public IReadOnlyList<Question> Asked => [.. _asked];

        public Task<bool> HasAgentsNotStoppedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
        {
            _asked.Enqueue(new Question(userId, context.Database.CurrentTransaction is not null, IsLocked(userId)));

            return Task.FromResult(NotStopped);
        }

        /// <summary>Whether another session holds a lock on the row of an account that keeps this one from locking it.</summary>
        private bool IsLocked(Guid userId)
        {
            using var connection = Database!.Open();
            using var command = new NpgsqlCommand("SELECT 1 FROM users WHERE id = @id FOR UPDATE NOWAIT", connection);
            command.Parameters.AddWithValue("id", userId);

            try
            {
                command.ExecuteScalar();

                return false;
            }
            catch (PostgresException refused) when (refused.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return true;
            }
        }
    }
}
