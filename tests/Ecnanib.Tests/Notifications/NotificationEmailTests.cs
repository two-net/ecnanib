using System.Net;
using System.Text.Json;
using Ecnanib.Accounts;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// A user can opt in to email for critical events, and no such email is sent without the opt-in
/// (FR-NTF-3). The page of the choice turns it on and off, a job sends what is about a critical
/// event to the users who opted in, in one message for a user, and an email holds no secret.
/// See "Email for critical events" in docs/notifications.md.
/// </summary>
public class NotificationEmailTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Page = "/account/notifications";
    private const string OnForm = "email-on";
    private const string OffForm = "email-off";

    // What the configuration file of the tests calls the service: the subject of a message starts with it.
    private const string Service = "Ecnanib Test";

    private const string Halted = "The agent reached its daily loss limit in BTCUSDT (Demo): it is halted.";
    private const string Warned = "The long position in BTCUSDT (Demo) approaches liquidation.";
    private const string Opened = "A position in BTCUSDT (Demo) was opened: 0.02 at 52000.";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_email_for_critical_events_is_sent_by_a_job_every_minute()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var job = application.NotificationEmail;

        // The application registers it with the background work. The scheduler of a test does
        // not run it: see EcnanibApplicationFactory.
        Assert.Equal("notification-email", job.Name);
        Assert.Equal("every 00:01:00", job.Schedule.ToString());
        Assert.Equal(Morning.AddMinutes(1), job.Schedule.NextRun(Morning, Morning.AddSeconds(30)));
    }

    [Fact]
    public async Task No_user_is_sent_email_for_a_critical_event_until_they_opt_in()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // The page says that it is off, and the page of the account says so too.
        var page = AgentBrowser.Read(await client.PageAsync(Page));

        Assert.Contains("<title>Email for critical events</title>", page);
        Assert.Contains("Off. You are sent no email for critical events.", page);
        Assert.Contains("Email for critical events: off.", await client.PageAsync("/account"));

        // A critical event, for a user who has not opted in: it is a notification, and no email.
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        await application.SendNotificationEmailsAsync();

        Assert.Empty(Critical(application));

        var stored = Assert.Single(await StoredAsync(application, ada.Id));

        Assert.False(stored.EmailPending);
        Assert.Null(stored.EmailedAt);

        // Opting in afterwards sends nothing of what was raised before.
        await client.SubmitAsync(Page, OnForm);
        await application.SendNotificationEmailsAsync();

        Assert.Empty(Critical(application));
    }

    [Fact]
    public async Task A_user_who_opted_in_is_sent_the_critical_events_in_one_message()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var (path, on) = await client.SubmitAsync(Page, OnForm);

        // The page as it is now, which says where an email goes.
        Assert.Equal(Page, path);
        Assert.Contains("On. Critical events are sent to ada@example.com.", AgentBrowser.Read(on));
        Assert.Contains("Email for critical events: on.", await client.PageAsync("/account"));
        Assert.Equal(1, application.Database.Scalar<long>("SELECT count(*) FROM email_opt_ins"));

        // Two critical events, and one that is none, a minute apart.
        clock.Set(Morning.AddMinutes(1));
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        clock.Set(Morning.AddMinutes(2));
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.PositionOpened, agentId: null, mode: null,Opened, Cancellation);
        clock.Set(Morning.AddMinutes(3));
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.LiquidationWarning, agentId: null, mode: null,Warned, Cancellation);

        // Nothing is sent by what raises one: the job sends.
        Assert.Empty(Critical(application));

        clock.Set(Morning.AddMinutes(4));
        await application.SendNotificationEmailsAsync();

        // One message, to her address, with both critical events, the older first, and each
        // with what it is about and when.
        var message = Assert.Single(Critical(application));

        Assert.Equal(Ada, message.To);
        Assert.Equal($"{Service}: 2 critical events", message.Subject);
        Assert.Contains($"Agent halted, {PageTimes.Utc.At(Morning.AddMinutes(1))}\n{Halted}\n", message.TextBody);
        Assert.Contains($"Liquidation warning, {PageTimes.Utc.At(Morning.AddMinutes(3))}\n{Warned}\n", message.TextBody);
        Assert.True(message.TextBody.IndexOf(Halted, StringComparison.Ordinal) < message.TextBody.IndexOf(Warned, StringComparison.Ordinal));

        // What is no critical event is not in it.
        Assert.DoesNotContain(Opened, message.TextBody);
        Assert.DoesNotContain("Position opened", message.TextBody);

        // It leads to the notifications, and to where it is turned off, each on a line of its own.
        var lines = message.TextBody.Split('\n');

        Assert.Contains($"{AccountsApplication.PublicUrl}/notifications", lines);
        Assert.Contains($"{AccountsApplication.PublicUrl}/account/notifications", lines);

        // The rows say what was sent, and when.
        var stored = await StoredAsync(application, ada.Id);

        Assert.Equal(
            [
                (NotificationType.AgentHalted, false, (DateTimeOffset?)Morning.AddMinutes(4)),
                (NotificationType.PositionOpened, false, null),
                (NotificationType.LiquidationWarning, false, Morning.AddMinutes(4)),
            ],
            stored.Select(notification => (notification.Type, notification.EmailPending, notification.EmailedAt)));

        // A run that is done again sends nothing again.
        await application.SendNotificationEmailsAsync();

        Assert.Single(Critical(application));

        // One event alone is named in the subject.
        clock.Set(Morning.AddMinutes(5));
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.KeyInvalid, agentId: null, mode: null,"Your key for Claude is invalid.", Cancellation);
        await application.SendNotificationEmailsAsync();

        Assert.Equal($"{Service}: Key invalid", Critical(application)[^1].Subject);
    }

    [Theory]
    [InlineData(NotificationType.AgentHalted, true)]
    [InlineData(NotificationType.AttentionRequired, true)]
    [InlineData(NotificationType.KeyInvalid, true)]
    [InlineData(NotificationType.ModelUnavailable, true)]
    [InlineData(NotificationType.ProtectionFailure, true)]
    [InlineData(NotificationType.LiquidationWarning, true)]
    [InlineData(NotificationType.EntrySkipped, false)]
    [InlineData(NotificationType.ReconciliationMismatch, false)]
    [InlineData(NotificationType.RolesReassigned, false)]
    [InlineData(NotificationType.PositionOpened, false)]
    [InlineData(NotificationType.PositionClosed, false)]
    [InlineData(NotificationType.AgentResumed, false)]
    public async Task An_email_is_sent_for_what_stops_an_agent_or_puts_money_at_risk_and_for_nothing_else(NotificationType type, bool critical)
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveEmailOptInAsync(ada.Id, Morning);

        await application.Notifications.RaiseAsync(ada.Id, type, agentId: null, mode: null,"Something that happened.", Cancellation);
        await application.SendNotificationEmailsAsync();

        Assert.Equal(critical, NotificationTypes.IsCritical(type));
        Assert.Equal(critical ? 1 : 0, Critical(application).Count);
    }

    [Fact]
    public void Every_kind_of_notification_is_a_critical_event_or_is_none()
    {
        // A kind that says neither would fail what raises it. The page of the choice lists the critical ones.
        Assert.All(Enum.GetValues<NotificationType>(), type => NotificationTypes.IsCritical(type));
        Assert.All(Enum.GetValues<NotificationType>(), type => Assert.NotEmpty(NotificationTypes.Describe(type)));
    }

    [Fact]
    public async Task The_page_names_the_critical_events()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var page = AgentBrowser.Read(await client.PageAsync(Page));

        Assert.All(
            Enum.GetValues<NotificationType>(),
            type =>
            {
                if (NotificationTypes.IsCritical(type))
                {
                    Assert.Contains($"<li>{NotificationTypes.Describe(type)}</li>", page);
                }
                else
                {
                    Assert.DoesNotContain($"<li>{NotificationTypes.Describe(type)}</li>", page);
                }
            });
    }

    [Fact]
    public async Task A_user_who_opts_out_before_it_is_sent_is_sent_nothing()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.SubmitAsync(Page, OnForm);

        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        Assert.True(Assert.Single(await StoredAsync(application, ada.Id)).EmailPending);

        // She turns it off before the job has run.
        var (path, off) = await client.SubmitAsync(Page, OffForm);

        Assert.Equal(Page, path);
        Assert.Contains("Off. You are sent no email for critical events.", AgentBrowser.Read(off));
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM email_opt_ins"));

        await application.SendNotificationEmailsAsync();

        // Nothing is sent, and nothing is left to be sent: turning it on again sends nothing of it.
        Assert.Empty(Critical(application));
        Assert.False(Assert.Single(await StoredAsync(application, ada.Id)).EmailPending);

        await client.SubmitAsync(Page, OnForm);
        await application.SendNotificationEmailsAsync();

        Assert.Empty(Critical(application));
    }

    [Fact]
    public async Task A_choice_that_is_made_twice_changes_nothing_the_second_time()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var before = await client.PageAsync(Page);
        await client.SubmitAsync(Page, OnForm);

        // The form of the page as it was is the form of no page now.
        using (var again = await client.SendFormAsync(Page, OnForm, Browser.AntiforgeryTokenIn(before)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }

        Assert.Equal(1, application.Database.Scalar<long>("SELECT count(*) FROM email_opt_ins"));
    }

    [Fact]
    public async Task An_address_that_is_not_verified_is_sent_nothing()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));

        // Registered, and the link of the message not opened.
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada)).Outcome);
        var ada = StoredUser.Of(application.Database, Ada)!;
        Assert.Null(ada.EmailVerifiedAt);

        await application.GiveEmailOptInAsync(ada.Id, Morning);
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        await application.SendNotificationEmailsAsync();

        // Nothing says yet that the address is hers, so nothing about her agents goes to it.
        Assert.Empty(Critical(application));
        Assert.False(Assert.Single(await StoredAsync(application, ada.Id)).EmailPending);

        // The page says so.
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        Assert.Contains("Your email address is not verified yet", AgentBrowser.Read(await client.PageAsync(Page)));
    }

    [Fact]
    public async Task A_message_that_is_not_taken_is_sent_with_a_later_run()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveEmailOptInAsync(ada.Id, Morning);
        await application.GiveEmailOptInAsync(grace.Id, Morning);

        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        clock.Set(Morning.AddMinutes(1));
        await application.Notifications.RaiseAsync(grace.Id, NotificationType.LiquidationWarning, agentId: null, mode: null,Warned, Cancellation);

        // The email delivery service is away: the run fails nothing, says so once, and leaves
        // both to be sent.
        application.Email.Failure = new InvalidOperationException("The mail server is away.");
        await application.SendNotificationEmailsAsync();

        Assert.Empty(Critical(application));
        Assert.True(Assert.Single(await StoredAsync(application, ada.Id)).EmailPending);
        Assert.True(Assert.Single(await StoredAsync(application, grace.Id)).EmailPending);
        Assert.Single(application.Events(), said => said.Area == "Ecnanib.Notifications.NotificationEmails" && said.Level == "Error");

        // It is back: the next run sends both, each to its own user.
        application.Email.Failure = null;
        clock.Set(Morning.AddMinutes(2));
        await application.SendNotificationEmailsAsync();

        Assert.Equal([Ada, Grace], Critical(application).Select(message => message.To));
        Assert.Contains(Halted, Critical(application)[0].TextBody);
        Assert.DoesNotContain(Warned, Critical(application)[0].TextBody);
        Assert.Contains(Warned, Critical(application)[1].TextBody);
        Assert.DoesNotContain(Halted, Critical(application)[1].TextBody);
        Assert.Equal(Morning.AddMinutes(2), Assert.Single(await StoredAsync(application, ada.Id)).EmailedAt);
    }

    [Fact]
    public async Task What_could_not_be_sent_within_a_day_is_given_up()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveEmailOptInAsync(ada.Id, Morning);
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);

        application.Email.Failure = new InvalidOperationException("The mail server is away.");
        await application.SendNotificationEmailsAsync();

        // A day later it is back, and what the notification told of is old: it is not sent.
        application.Email.Failure = null;
        clock.Set(Morning.AddDays(1));
        await application.SendNotificationEmailsAsync();

        Assert.Empty(Critical(application));

        var stored = Assert.Single(await StoredAsync(application, ada.Id));

        Assert.False(stored.EmailPending);
        Assert.Null(stored.EmailedAt);
        Assert.Single(application.Events(), said => said.Area == "Ecnanib.Notifications.NotificationEmails" && said.Level == "Warning");
    }

    [Fact]
    public async Task The_log_holds_neither_the_address_nor_what_a_notification_says()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveEmailOptInAsync(ada.Id, Morning);
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        await application.SendNotificationEmailsAsync();

        Assert.Single(Critical(application));

        // What sends it names the user by id, and how many it sent.
        var sent = Assert.Single(application.Events(), said => said.Area == "Ecnanib.Notifications.NotificationEmails");

        Assert.Equal(ada.Id.ToString(), sent.Text("UserId"));
        Assert.DoesNotContain(Ada, sent.Json.GetRawText());
        Assert.DoesNotContain(Halted, application.LogText());
    }

    [Fact]
    [ProvesOwnershipOf("email_opt_ins")]
    public async Task The_choice_of_a_user_is_theirs_alone()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var grace = await application.RegisterVerifiedAsync(Grace);
        using var graces = application.CreateClient();
        await graces.SignInAsync(Grace);
        await graces.SubmitAsync(Page, OnForm);
        var before = application.RowsOf(grace.Id);

        Assert.Single(before["email_opt_ins"]);

        // Ada's pages say nothing of Grace's choice: hers is off, and her export names none.
        clock.Set(Morning.AddHours(1));
        var ada = await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        var page = AgentBrowser.Read(await client.PageAsync(Page));

        Assert.Contains("Off. You are sent no email for critical events.", page);
        Assert.DoesNotContain(Grace, page);
        Assert.Equal(0, ExportOf(await client.PageAsync("/account/export")).GetArrayLength());

        // She turns hers on and off again, and nothing of Grace's changes: no form names a user.
        await client.SubmitAsync(Page, OnForm);

        var optIn = Assert.Single(ExportOf(await client.PageAsync("/account/export")).EnumerateArray());

        Assert.Equal(Morning.AddHours(1), optIn.GetProperty("optedInAt").GetDateTimeOffset());
        Assert.Single(application.RowsOf(ada.Id)["email_opt_ins"]);

        await client.SubmitAsync(Page, OffForm);

        Assert.Empty(application.RowsOf(ada.Id)["email_opt_ins"]);
        Assert.Equal(before, application.RowsOf(grace.Id));

        // A critical event of Ada's is sent to nobody, and one of Grace's to Grace alone.
        await application.Notifications.RaiseAsync(ada.Id, NotificationType.AgentHalted, agentId: null, mode: null,Halted, Cancellation);
        await application.Notifications.RaiseAsync(grace.Id, NotificationType.LiquidationWarning, agentId: null, mode: null,Warned, Cancellation);
        await application.SendNotificationEmailsAsync();

        var message = Assert.Single(Critical(application));

        Assert.Equal(Grace, message.To);
        Assert.DoesNotContain(Halted, message.TextBody);
    }

    [Fact]
    public async Task A_visitor_who_opens_the_page_is_sent_to_sign_in()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        using var client = application.CreateClient();

        var (address, shown) = await client.OpenAsync(Page);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(Page)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);
    }

    [Fact]
    public async Task A_form_without_the_token_of_its_page_changes_nothing()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Something that is not the page sends the form, with a token that the page did not give.
        using var forged = await client.SendFormAsync(Page, OnForm, "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM email_opt_ins"));
    }

    /// <summary>The messages about critical events that the email delivery service took, in order: not those of an account.</summary>
    private static IReadOnlyList<EmailMessage> Critical(AccountsApplication application) =>
        [.. application.Email.Sent.Where(message => message.Subject.StartsWith($"{Service}: ", StringComparison.Ordinal))];

    /// <summary>The section of an export that says whether its user opted in.</summary>
    private static JsonElement ExportOf(string export) => JsonDocument.Parse(export).RootElement.GetProperty("emailOptIn");

    /// <summary>The notifications of a user as they are stored, in the order that they were raised in.</summary>
    private static async Task<IReadOnlyList<Notification>> StoredAsync(AccountsApplication application, Guid userId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<Notification>()
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id)
            .ToListAsync(Cancellation);
    }
}
