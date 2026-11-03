using System.Net;
using Ecnanib.Components.Account;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUD-03: a user sees their own audit history, and only theirs.</summary>
public sealed class ActivityTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private const string Page = "/account/activity";
    private const string ShowOlder = "Show older activity";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IAccountActivity Activity => app.Services.GetRequiredService<IAccountActivity>();

    [Fact]
    public async Task A_user_sees_their_own_activity_and_nobody_elses()
    {
        using var mine = Visitor.To(app);
        using var theirs = Visitor.To(app);
        mine.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mine Browser/1.0");
        theirs.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Their Browser/2.0");

        var email = await mine.SignUpAsync();
        await theirs.SignUpAsync();
        (await theirs.SignInAsync(email, "not my password at all")).Dispose();

        var page = WebUtility.HtmlDecode(await mine.PageAsync(Page));

        Assert.Contains("Signed in", page, StringComparison.Ordinal);
        Assert.Contains("Mine Browser/1.0", page, StringComparison.Ordinal);

        // Someone else's failed attempt on this account belongs here, but not their own sign-in.
        Assert.Contains("Failed sign-in attempt", page, StringComparison.Ordinal);
        Assert.Single(Occurrences(page, "Their Browser/2.0"));
        Assert.DoesNotContain(ShowOlder, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_agent_is_only_text()
    {
        const string Hostile = "<script>alert(1)</script>";

        using var visitor = Visitor.To(app);
        visitor.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Hostile);
        await visitor.SignUpAsync();

        var page = await visitor.PageAsync(Page);

        Assert.DoesNotContain(Hostile, page, StringComparison.Ordinal);
        Assert.Contains(Hostile, WebUtility.HtmlDecode(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_page_offers_older_activity()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        await RecordAsync(userId, Activity.PageSize);

        Assert.Contains(ShowOlder, await visitor.PageAsync(Page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Older_activity_comes_in_pages()
    {
        var userId = Guid.NewGuid();

        await RecordAsync(userId, Activity.PageSize + 1);

        var first = await Activity.ListAsync(userId, before: null, Cancellation);
        var rest = await Activity.ListAsync(userId, first[^1].Id, Cancellation);

        Assert.Equal(Activity.PageSize, first.Count);
        Assert.Single(rest);
        Assert.Equal(first.Select(record => record.Id).OrderDescending(), first.Select(record => record.Id));
        Assert.True(rest[0].Id < first[^1].Id);
    }

    [Fact]
    public async Task The_account_page_links_to_it()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        Assert.Contains($"href=\"{Page}\"", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>An issue that adds an action has to give it text, or this fails.</summary>
    [Fact]
    public void Every_action_has_text_of_its_own()
    {
        var texts = Enum.GetValues<AuditAction>().Select(ActivityText.Of).ToList();

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    private async Task RecordAsync(Guid userId, int count)
    {
        await using var context = await app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContextAsync(Cancellation);
        var trail = app.Services.GetRequiredService<IAuditTrail>();

        for (var i = 0; i < count; i++)
        {
            trail.Add(context, AuditEntry.ByUser(AuditAction.PasswordChanged, userId));
        }

        await context.SaveChangesAsync(Cancellation);
    }
}
