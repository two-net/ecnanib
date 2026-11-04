using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// The application with email on, its mail kept, and the notification job run only when a test says.
/// The administrator's address is listed, and a test creates its account when it needs one.
/// </summary>
public sealed class NotificationEmailApp : AppFactory
{
    public const string Job = "NotificationEmailJob";

    public NotificationEmailApp(PostgresFixture postgres)
        : this(new CapturedEmail(), Visitor.NewEmail(), postgres.NewConnectionString())
    {
    }

    private NotificationEmailApp(CapturedEmail email, string administrator, string connectionString)
        : base(
            connectionString,
            email.ReplaceSender,
            AccountApp.Settings(AccountApp.EmailOn(new() { ["Admin:AdminEmails:0"] = administrator })),
            manualJobs: [Job])
    {
        Email = email;
        Administrator = administrator;
    }

    /// <summary>Every message the application has sent. Tests share it, so look for your own address.</summary>
    public CapturedEmail Email { get; }

    public string Administrator { get; }

    public string ConnectionString => this.ConnectionString();

    /// <summary>One run, under a name of its own, as SessionExpiryJobTests does. Returns what it recorded.</summary>
    public static async Task<string?> RunAsync(AppFactory app)
    {
        var name = $"test-notification-email-{Guid.NewGuid():N}";
        var status = await app.Services.GetRequiredService<IJobRunner>()
            .RunAsync(name, TimeSpan.FromSeconds(30), app.ManualJob(Job).RunAsync);

        Assert.Equal(JobStatus.Succeeded, status);

        var details = await Sql.ColumnAsync(
            app.ConnectionString(),
            $"SELECT coalesce(details, '') FROM job_runs WHERE job_name = '{name}'",
            TestContext.Current.CancellationToken);

        return Assert.Single(details);
    }
}

/// <summary>
/// FR-NTF-02 and FR-PREF-06: which notifications are emailed, to whom, and what the email holds. Each
/// test runs the job after raising, so nothing waits into the next test.
/// </summary>
public sealed class NotificationEmailTests(NotificationEmailApp app, PostgresFixture postgres) : IClassFixture<NotificationEmailApp>
{
    private const string Origin = AccountApp.PublicBaseUrl;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private INotifications Inbox => app.Services.GetRequiredService<INotifications>();

    private IPreferences Preferences => app.Services.GetRequiredService<IPreferences>();

    private static UserPreferences Look => new(Theme.System, AnimationLevel.System, null);

    [Fact]
    public async Task The_defaults_email_what_needs_action_and_nothing_else()
    {
        var (userId, address) = await VerifiedUserAsync();

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, "/agents/1", "Scalper"), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/2", "Scalper", demo: true), Cancellation);

        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(address));

        Assert.Equal("Trade waiting for your approval on Ecnanib", message.Subject);
        Assert.StartsWith("You have a new notification on Ecnanib.", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Trade waiting for your approval: Scalper (Demo)", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Trade opened", message.TextBody, StringComparison.Ordinal);
        Assert.Equal([new Uri($"{Origin}/approvals/2"), new Uri($"{Origin}/account/preferences")], CapturedEmail.LinksIn(message));

        // Both are done with. Only the one that went is marked as emailed.
        var rows = await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation);
        Assert.All(rows, row => Assert.False(row.EmailDue));
        Assert.Equal([false, true], rows.Select(row => row.Emailed));
    }

    [Fact]
    public async Task What_a_user_chose_is_emailed()
    {
        var (userId, address) = await VerifiedUserAsync();

        await Preferences.SaveAsync(userId, Look, new HashSet<NotificationType> { NotificationType.TradeOpened }, Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, "/agents/1"), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/2"), Cancellation);

        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(address));
        Assert.Equal("Trade opened on Ecnanib", message.Subject);
        Assert.Contains($"Trade opened\n", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("approval", message.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_user_who_chose_none_gets_no_email()
    {
        var (userId, address) = await VerifiedUserAsync();

        await Preferences.SaveAsync(userId, Look, new HashSet<NotificationType>(), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);

        await NotificationEmailApp.RunAsync(app);

        Assert.Empty(app.Email.To(address));
        Assert.False(Assert.Single(await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation)).EmailDue);
    }

    /// <summary>Anyone can register someone else's address, so an unverified one could be a stranger's.</summary>
    [Fact]
    public async Task An_unverified_address_gets_no_email()
    {
        var address = Visitor.NewEmail();
        var userId = await TestUsers.AddAsync(app.ConnectionString, address, verified: false, Cancellation);

        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);
        await NotificationEmailApp.RunAsync(app);

        Assert.Empty(app.Email.To(address));

        var row = Assert.Single(await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation));
        Assert.Equal((false, false), (row.EmailDue, row.Emailed));

        // Verifying later doesn't send what was raised before.
        await TestUsers.VerifyAsync(app.ConnectionString, userId, Cancellation);
        await NotificationEmailApp.RunAsync(app);

        Assert.Empty(app.Email.To(address));
    }

    /// <summary>One event can fire for many agents, so a burst becomes one email.</summary>
    [Fact]
    public async Task Several_arrive_as_one_email_newest_first()
    {
        var (userId, address) = await VerifiedUserAsync();

        for (var number = 1; number <= 3; number++)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, $"/approvals/{number}", $"Agent {number}"), Cancellation);
        }

        await NotificationEmailApp.RunAsync(app);
        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(address));

        Assert.Equal("3 new notifications on Ecnanib", message.Subject);
        Assert.StartsWith("You have 3 new notifications on Ecnanib.", message.TextBody, StringComparison.Ordinal);
        Assert.Equal(
            [
                new Uri($"{Origin}/approvals/3"),
                new Uri($"{Origin}/approvals/2"),
                new Uri($"{Origin}/approvals/1"),
                new Uri($"{Origin}/account/preferences"),
            ],
            CapturedEmail.LinksIn(message));
        Assert.Matches(@"Trade waiting for your approval: Agent 3\n\d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ\n", message.TextBody);
    }

    [Fact]
    public async Task Past_twenty_the_rest_are_counted_with_a_link_to_the_list()
    {
        var (userId, address) = await VerifiedUserAsync();

        for (var number = 1; number <= 25; number++)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.KillSwitchExecuted, userId, $"/agents/{number}"), Cancellation);
        }

        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(address));
        var links = CapturedEmail.LinksIn(message);

        Assert.Equal(22, links.Count);
        Assert.Equal(new Uri($"{Origin}/agents/25"), links[0]);
        Assert.Contains($"And 5 more: {Origin}/notifications", message.TextBody, StringComparison.Ordinal);
        Assert.All(
            await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation),
            row => Assert.Equal((false, true), (row.EmailDue, row.Emailed)));
    }

    /// <summary>After an outage, news from yesterday stays in the app rather than arriving as a burst.</summary>
    [Fact]
    public async Task A_notification_older_than_a_day_isnt_emailed()
    {
        var (userId, address) = await VerifiedUserAsync();

        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);
        await NotificationRows.AgeAsync(app.ConnectionString, userId, TimeSpan.FromHours(25), Cancellation);

        await NotificationEmailApp.RunAsync(app);

        Assert.Empty(app.Email.To(address));
        Assert.False(Assert.Single(await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation)).EmailDue);
    }

    [Fact]
    public async Task An_administrator_is_emailed_their_own_types()
    {
        var administratorId = await AdministratorAsync();
        var before = app.Email.To(app.Administrator).Count;

        await Inbox.RaiseForAdministratorsAsync(new AdministratorNotice(NotificationType.CatalogSyncFailed, "/admin/catalog", "Anthropic"), Cancellation);
        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(app.Administrator).Skip(before));
        Assert.Equal("Model catalog sync failed on Ecnanib", message.Subject);
        Assert.Contains("Model catalog sync failed: Anthropic", message.TextBody, StringComparison.Ordinal);

        // A type nobody can raise for a user is never offered to one, whatever is stored.
        var (userId, _) = await VerifiedUserAsync();
        await NotificationRows.SetEmailChoicesAsync(app.ConnectionString, userId, ["CatalogSyncFailed", "TradeOpened"], Cancellation);

        Assert.Equal([NotificationType.TradeOpened], await Preferences.GetEmailNotificationsAsync(userId, Cancellation));
        Assert.Contains(NotificationType.CatalogSyncFailed, await Preferences.GetEmailNotificationsAsync(administratorId, Cancellation));
    }

    /// <summary>A type without text in Emails.resx would fail the run, so one of each proves they all have it.</summary>
    [Fact]
    public async Task Every_type_has_its_email_text()
    {
        var administratorId = await AdministratorAsync();
        var before = app.Email.To(app.Administrator).Count;
        var all = EmailNotificationChoices.AvailableTo(administrator: true).ToHashSet();

        Assert.Equal(PreferencesResult.Saved, await Preferences.SaveAsync(administratorId, Look, all, Cancellation));

        foreach (var type in Enum.GetValues<NotificationType>().Except(EmailNotificationChoices.AdministratorOnly))
        {
            await Inbox.RaiseAsync(new Notice(type, administratorId, "/agents/1"), Cancellation);
        }

        foreach (var type in EmailNotificationChoices.AdministratorOnly)
        {
            await Inbox.RaiseForAdministratorsAsync(new AdministratorNotice(type, "/admin/health"), Cancellation);
        }

        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(app.Administrator).Skip(before));
        var headings = Regex.Matches(message.TextBody, @"\n\n(?<heading>[^\n]+)\n\d{4}-").Select(match => match.Groups["heading"].Value).ToList();

        Assert.Equal(Enum.GetValues<NotificationType>().Length, headings.Count);
        Assert.Equal(headings.Count, headings.Distinct().Count());
        Assert.All(headings, heading => Assert.False(string.IsNullOrWhiteSpace(heading)));
    }

    /// <summary>AC-03: the email carries what was stored, which the backstop already masked.</summary>
    [Fact]
    public async Task A_subject_that_looks_like_a_secret_doesnt_reach_the_email()
    {
        var (userId, address) = await VerifiedUserAsync();
        var canary = CanaryCases.NewCanary();

        foreach (var (_, render) in CanaryCases.SecretShapes)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.CredentialInvalid, userId, "/credentials/1", render(canary)), Cancellation);
        }

        await NotificationEmailApp.RunAsync(app);

        var message = Assert.Single(app.Email.To(address));
        Assert.DoesNotContain(canary, message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, message.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_records_counts_and_nothing_else()
    {
        var (userId, address) = await VerifiedUserAsync();

        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1", "Scalper"), Cancellation);

        var details = await NotificationEmailApp.RunAsync(app);

        Assert.Matches(@"^\d+ emails, \d+ notifications, \d+ expired$", details);
        Assert.Single(app.Email.To(address));
    }

    /// <summary>Each message is a job run with an SMTP connection, so one run starts only so many.</summary>
    [Fact]
    public async Task Fifty_users_go_per_run_and_the_rest_wait_for_the_next()
    {
        var email = new CapturedEmail();
        await using var own = AccountApp.CreateWithEmail(postgres, email, manualJobs: [NotificationEmailApp.Job]);
        var inbox = own.Services.GetRequiredService<INotifications>();
        var connectionString = own.ConnectionString();

        for (var count = 0; count < 51; count++)
        {
            var userId = await TestUsers.AddAsync(connectionString, Visitor.NewEmail(), verified: true, Cancellation);
            await inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);
        }

        Assert.Equal("50 emails, 50 notifications, 0 expired", await NotificationEmailApp.RunAsync(own));
        Assert.Equal(50, email.Sent.Count);

        Assert.Equal("1 emails, 1 notifications, 0 expired", await NotificationEmailApp.RunAsync(own));
        Assert.Equal(51, email.Sent.Select(message => message.To).Distinct().Count());
    }

    [Fact]
    public async Task With_email_off_the_job_sends_nothing()
    {
        var email = new CapturedEmail();
        await using var off = AccountApp.Create(postgres, services: email.ReplaceSender, manualJobs: [NotificationEmailApp.Job]);
        var connectionString = off.ConnectionString();
        var userId = await TestUsers.AddAsync(connectionString, Visitor.NewEmail(), verified: true, Cancellation);

        await off.Services.GetRequiredService<INotifications>()
            .RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);

        // As if it had been raised while email was on.
        await NotificationRows.SetEmailDueAsync(connectionString, userId, Cancellation);
        await NotificationEmailApp.RunAsync(off);

        Assert.Empty(email.Sent);
        Assert.True(Assert.Single(await NotificationRows.OfAsync(connectionString, userId, Cancellation)).EmailDue);

        // Nor does its schedule ever start it, so it adds nothing to job_runs.
        Assert.Equal(DateTimeOffset.MaxValue, off.ManualJob(NotificationEmailApp.Job).Schedule.NextRun(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_public_address_with_a_path_still_gives_plain_links()
    {
        var email = new CapturedEmail();
        await using var own = AccountApp.CreateWithEmail(
            postgres,
            email,
            new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "http://ecnanib.test/base/" },
            manualJobs: [NotificationEmailApp.Job]);
        var address = Visitor.NewEmail();
        var userId = await TestUsers.AddAsync(own.ConnectionString(), address, verified: true, Cancellation);

        await own.Services.GetRequiredService<INotifications>()
            .RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1"), Cancellation);
        await NotificationEmailApp.RunAsync(own);

        Assert.Equal(new Uri("http://ecnanib.test/approvals/1"), CapturedEmail.LinksIn(Assert.Single(email.To(address)))[0]);
    }

    /// <summary>An address isn't a secret the rules mask, and a subject is user text, so only not logging them keeps them out.</summary>
    [Fact]
    public async Task The_log_holds_neither_the_address_nor_the_subject()
    {
        var email = new CapturedEmail();
        var address = Visitor.NewEmail();
        var subject = $"Agent{Guid.NewGuid():N}";

        await using var logged = new LoggedApp(
            postgres.NewConnectionString(),
            AccountApp.Settings(AccountApp.EmailOn(new() { ["Logging:MinimumLevel"] = "Verbose" })),
            email.ReplaceSender,
            manualJobs: [NotificationEmailApp.Job]);
        var userId = await TestUsers.AddAsync(logged.App.ConnectionString(), address, verified: true, Cancellation);

        await logged.App.Services.GetRequiredService<INotifications>()
            .RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, "/approvals/1", subject), Cancellation);
        await NotificationEmailApp.RunAsync(logged.App);

        Assert.Single(email.To(address));

        await logged.StopAsync();
        var text = logged.FileText();

        Assert.Contains("Emailed 1 notifications", text, StringComparison.Ordinal);
        Assert.DoesNotContain(address, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(subject, text, StringComparison.Ordinal);
    }

    private async Task<(Guid UserId, string Address)> VerifiedUserAsync()
    {
        var address = Visitor.NewEmail();

        return (await TestUsers.AddAsync(app.ConnectionString, address, verified: true, Cancellation), address);
    }

    /// <summary>The listed administrator's account, created by the first test that needs it. Tests in a class run one at a time.</summary>
    private async Task<Guid> AdministratorAsync()
    {
        var existing = await Sql.ColumnAsync(
            app.ConnectionString,
            $"SELECT id::text FROM users WHERE normalized_email = '{app.Administrator.ToLowerInvariant()}'",
            Cancellation);

        return existing.Count == 1
            ? Guid.Parse(existing[0])
            : await TestUsers.AddAsync(app.ConnectionString, app.Administrator, verified: true, Cancellation);
    }
}
