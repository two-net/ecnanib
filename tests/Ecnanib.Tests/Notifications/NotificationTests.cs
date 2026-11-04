using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// FR-NTF-01 and FR-NTF-03 with email off: raising, what's kept, and what each user can see and change
/// (NFR-SEC-04). NotificationEmailTests covers the email.
/// </summary>
public sealed class NotificationTests(SharedAccountApp app, PostgresFixture postgres) : IClassFixture<SharedAccountApp>
{
    private const string AgentLink = "/agents/42";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private INotifications Inbox => app.Services.GetRequiredService<INotifications>();

    private IDbContextFactory<EcnanibDbContext> Contexts => app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();

    public static TheoryData<string> RefusedLinks =>
    [
        string.Empty,
        "/",
        "agents/42",
        "https://example.com/agents/42",
        "//example.com/agents",
        "/\\example.com",
        "/agents//42",
        "/agents/../account",
        "/agents/42/",
        "/agents/4%2F2",
        "/agents/42?token=abc",
        "/agents/42#trades",
        "/agents/4 2",
        "/" + new string('a', Notice.LinkMaxLength),
    ];

    [Fact]
    public async Task A_user_sees_only_their_own_newest_first()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, mine, "/agents/1", "First"), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.TradeClosed, mine, "/agents/1", "Second", demo: true), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.StopMoved, theirs, "/agents/2", "Theirs"), Cancellation);

        var list = await Inbox.ListAsync(mine, before: null, Cancellation);

        Assert.Equal([NotificationType.TradeClosed, NotificationType.TradeOpened], list.Select(record => record.Type));
        Assert.Equal(("Second", true, "/agents/1"), (list[0].Subject, list[0].Demo, list[0].Link));
        Assert.All(list, record => Assert.Null(record.ReadAt));
        Assert.InRange(list[0].CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(2, await Inbox.CountUnreadAsync(mine, Cancellation));
        Assert.Equal("Theirs", Assert.Single(await Inbox.ListAsync(theirs, before: null, Cancellation)).Subject);
    }

    [Fact]
    public async Task Nobody_can_mark_another_users_notification_read()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, mine, AgentLink), Cancellation);
        var id = Assert.Single(await Inbox.ListAsync(mine, before: null, Cancellation)).Id;

        await Inbox.MarkReadAsync(theirs, id, Cancellation);
        await Inbox.MarkAllReadAsync(theirs, long.MaxValue, Cancellation);

        Assert.Equal(1, await Inbox.CountUnreadAsync(mine, Cancellation));

        await Inbox.MarkReadAsync(mine, id, Cancellation);

        Assert.Equal(0, await Inbox.CountUnreadAsync(mine, Cancellation));
        Assert.NotNull(Assert.Single(await Inbox.ListAsync(mine, before: null, Cancellation)).ReadAt);
    }

    /// <summary>One that arrives while the page is open hasn't been seen, so it stays unread.</summary>
    [Fact]
    public async Task Marking_all_read_stops_at_the_newest_one_shown()
    {
        var userId = Guid.NewGuid();

        for (var count = 0; count < 3; count++)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, AgentLink), Cancellation);
        }

        var shown = await Inbox.ListAsync(userId, before: null, Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.TradeClosed, userId, AgentLink), Cancellation);

        await Inbox.MarkAllReadAsync(userId, shown.Max(record => record.Id), Cancellation);

        var unread = Assert.Single(await Inbox.ListAsync(userId, before: null, Cancellation), record => record.ReadAt is null);
        Assert.Equal(NotificationType.TradeClosed, unread.Type);
    }

    [Fact]
    public async Task The_list_pages_back_by_the_last_id()
    {
        var userId = Guid.NewGuid();
        var total = Inbox.PageSize + 5;

        for (var count = 0; count < total; count++)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, AgentLink, $"Agent {count}"), Cancellation);
        }

        var first = await Inbox.ListAsync(userId, before: null, Cancellation);
        var second = await Inbox.ListAsync(userId, first[^1].Id, Cancellation);

        Assert.Equal(Inbox.PageSize, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Equal($"Agent {total - 1}", first[0].Subject);
        Assert.Equal("Agent 0", second[^1].Subject);
    }

    [Fact]
    public async Task One_added_to_a_context_exists_only_if_that_context_commits()
    {
        var userId = Guid.NewGuid();

        await using (var unsaved = await Contexts.CreateDbContextAsync(Cancellation))
        {
            Inbox.Add(unsaved, new Notice(NotificationType.TradeOpened, userId, AgentLink));
        }

        await using (var context = await Contexts.CreateDbContextAsync(Cancellation))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);

            Inbox.Add(context, new Notice(NotificationType.TradeOpened, userId, AgentLink));
            await context.SaveChangesAsync(Cancellation);
            await transaction.RollbackAsync(Cancellation);
        }

        Assert.Empty(await Inbox.ListAsync(userId, before: null, Cancellation));

        await using (var context = await Contexts.CreateDbContextAsync(Cancellation))
        {
            Inbox.Add(context, new Notice(NotificationType.TradeOpened, userId, AgentLink));
            await context.SaveChangesAsync(Cancellation);
        }

        Assert.Single(await Inbox.ListAsync(userId, before: null, Cancellation));
    }

    [Fact]
    public async Task With_email_off_nothing_waits_for_email()
    {
        var userId = Guid.NewGuid();

        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalPending, userId, AgentLink), Cancellation);

        Assert.False(Assert.Single(await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation)).EmailDue);
    }

    /// <summary>FR-NTF-03: a link is a path on this site, so it can't leave it or carry a token.</summary>
    [Theory]
    [MemberData(nameof(RefusedLinks))]
    public void A_link_that_isnt_a_plain_path_on_this_site_is_refused(string link)
    {
        Assert.Throws<ArgumentException>(() => new Notice(NotificationType.TradeOpened, Guid.NewGuid(), link));
        Assert.Throws<ArgumentException>(() => new AdministratorNotice(NotificationType.CatalogSyncFailed, link));
    }

    [Theory]
    [InlineData("/agents/42")]
    [InlineData("/approvals/0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("/admin/catalog_sync")]
    public void A_plain_path_is_accepted(string link) =>
        Assert.Equal(link, new Notice(NotificationType.TradeOpened, Guid.NewGuid(), link).Link);

    [Fact]
    public void Each_kind_of_notice_refuses_the_other_kinds_types()
    {
        Assert.Throws<ArgumentException>(() => new Notice(NotificationType.CatalogSyncFailed, Guid.NewGuid(), AgentLink));
        Assert.Throws<ArgumentException>(() => new Notice(NotificationType.ExchangeRateLimitBan, Guid.NewGuid(), AgentLink));
        Assert.Throws<ArgumentException>(() => new AdministratorNotice(NotificationType.TradeOpened, AgentLink));
    }

    /// <summary>PostgreSQL refuses a NUL, which would fail the save of whatever change raised it.</summary>
    [Fact]
    public async Task A_subject_keeps_only_what_can_be_stored_and_shown()
    {
        var userId = Guid.NewGuid();
        var surrogatePair = char.ConvertFromUtf32(0x1F916);
        var cut = new string('a', Notice.SubjectMaxLength - 1) + surrogatePair;

        Assert.Equal("Bot one", new Notice(NotificationType.TradeOpened, userId, AgentLink, " Bot\0 one\u0007\n").Subject);
        Assert.Equal("ab", new Notice(NotificationType.TradeOpened, userId, AgentLink, "a\ud800b\udc00").Subject);
        Assert.Equal($"Bot {surrogatePair}", new Notice(NotificationType.TradeOpened, userId, AgentLink, $"Bot {surrogatePair}").Subject);
        Assert.Equal(new string('a', Notice.SubjectMaxLength - 1), new Notice(NotificationType.TradeOpened, userId, AgentLink, cut).Subject);
        Assert.Equal(Notice.SubjectMaxLength, new Notice(NotificationType.TradeOpened, userId, AgentLink, new string('b', 500)).Subject!.Length);
        Assert.Null(new Notice(NotificationType.TradeOpened, userId, AgentLink, "\0 \u0001").Subject);

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, AgentLink, "Bot\0 one"), Cancellation);

        Assert.Equal("Bot one", Assert.Single(await Inbox.ListAsync(userId, before: null, Cancellation)).Subject);
    }

    /// <summary>AC-03: a backstop with the logging rules, which a subject that holds a secret by mistake meets.</summary>
    [Fact]
    public async Task A_subject_that_looks_like_a_secret_is_masked()
    {
        var userId = Guid.NewGuid();
        var canary = CanaryCases.NewCanary();
        var databasePassword = new NpgsqlConnectionStringBuilder(app.ConnectionString).Password!;

        foreach (var (_, render) in CanaryCases.SecretShapes)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.CredentialInvalid, userId, AgentLink, render(canary)), Cancellation);
        }

        await Inbox.RaiseAsync(new Notice(NotificationType.CredentialInvalid, userId, AgentLink, $"Key {databasePassword}"), Cancellation);

        var rows = await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation);

        Assert.Equal(CanaryCases.SecretShapes.Count + 1, rows.Count);
        Assert.All(rows, row => Assert.DoesNotContain(canary, row.Payload, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.DoesNotContain(databasePassword, row.Payload, StringComparison.Ordinal));
        Assert.Contains("Key ***", rows[^1].Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrators_are_notified_and_nobody_else()
    {
        var administrator = Visitor.NewEmail();
        var unverifiedAdministrator = Visitor.NewEmail();

        await using var configured = AccountApp.Create(
            postgres,
            new Dictionary<string, string?>
            {
                ["Admin:AdminEmails:0"] = administrator,
                ["Admin:AdminEmails:1"] = unverifiedAdministrator,
                ["Admin:BootstrapSecret"] = new string('s', 40),
            });
        var connectionString = configured.ConnectionString();
        var administratorId = await TestUsers.AddAsync(connectionString, administrator, verified: true, Cancellation);
        var unverifiedId = await TestUsers.AddAsync(connectionString, unverifiedAdministrator, verified: false, Cancellation);
        var userId = await TestUsers.AddAsync(connectionString, Visitor.NewEmail(), verified: true, Cancellation);
        var inbox = configured.Services.GetRequiredService<INotifications>();

        await inbox.RaiseForAdministratorsAsync(new AdministratorNotice(NotificationType.CatalogSyncFailed, "/admin/catalog", "Anthropic"), Cancellation);

        var record = Assert.Single(await inbox.ListAsync(administratorId, before: null, Cancellation));
        Assert.Equal((NotificationType.CatalogSyncFailed, "Anthropic", false), (record.Type, record.Subject, record.Demo));
        Assert.Empty(await inbox.ListAsync(unverifiedId, before: null, Cancellation));
        Assert.Empty(await inbox.ListAsync(userId, before: null, Cancellation));
    }

    [Fact]
    public async Task The_page_lists_them_with_the_subject_as_text()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, "/agents/7", "<b>Scalper</b>", demo: true), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.ApprovalExpired, userId, "/approvals/9"), Cancellation);
        await Inbox.MarkReadAsync(userId, (await Inbox.ListAsync(userId, before: null, Cancellation))[0].Id, Cancellation);

        var page = await visitor.PageAsync("/notifications");
        var text = WebUtility.HtmlDecode(page);

        Assert.Contains("&lt;b&gt;Scalper&lt;/b&gt;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Scalper", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/agents/7\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/approvals/9\"", page, StringComparison.Ordinal);
        Assert.Contains("Trade opened", text, StringComparison.Ordinal);
        Assert.Contains("Approval expired", text, StringComparison.Ordinal);
        Assert.Contains(">Demo<", text, StringComparison.Ordinal);

        // Only the trade is unread, and "New" says so in words.
        Assert.Single(Occurrences(text, ">New<"));
        Assert.Contains("Mark all as read", text, StringComparison.Ordinal);

        // Email is off, so there's nothing to choose.
        Assert.DoesNotContain("/account/preferences", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_user_has_none()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        var text = WebUtility.HtmlDecode(await visitor.PageAsync("/notifications"));

        Assert.Contains("Nothing yet.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark all as read", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_menu_counts_what_is_unread()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        Assert.Contains(">Notifications</a>", await visitor.PageAsync("/account"), StringComparison.Ordinal);

        await Inbox.RaiseAsync(new Notice(NotificationType.TradeOpened, userId, AgentLink), Cancellation);
        await Inbox.RaiseAsync(new Notice(NotificationType.TradeClosed, userId, AgentLink), Cancellation);

        Assert.Contains(">Notifications (2 new)</a>", await visitor.PageAsync("/account"), StringComparison.Ordinal);

        for (var count = 0; count < 98; count++)
        {
            await Inbox.RaiseAsync(new Notice(NotificationType.StopMoved, userId, AgentLink), Cancellation);
        }

        // Razor encodes the plus sign.
        Assert.Contains(">Notifications (99+ new)</a>", WebUtility.HtmlDecode(await visitor.PageAsync("/account")), StringComparison.Ordinal);

        await Inbox.MarkAllReadAsync(userId, long.MaxValue, Cancellation);

        Assert.Contains(">Notifications</a>", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_visitor_has_no_count_and_no_list()
    {
        using var visitor = Visitor.To(app);

        Assert.DoesNotContain("/notifications", await visitor.PageAsync("/"), StringComparison.Ordinal);

        using var response = await visitor.GetAsync("/notifications");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(response), StringComparison.Ordinal);
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }
}
