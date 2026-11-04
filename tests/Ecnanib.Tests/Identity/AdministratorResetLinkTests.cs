using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Email;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// FR-AUTH-06 and EXT-EML-01 with email off: an administrator issues a single-use link to hand to the
/// user out of band.
/// </summary>
public sealed partial class AdministratorResetLinkTests(PostgresFixture postgres)
{
    private const string Secret = "a bootstrap secret of plenty of characters";
    private const string Page = "/admin/password-reset";
    private const string NewPassword = "a new password of some length";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_administrator_issues_a_link_that_works_once()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);
        var email = await RegisterAsync(app);

        using var response = await administrator.IssueResetLinkAsync(email);
        var page = await response.Content.ReadAsStringAsync(Cancellation);
        var link = LinkIn(page);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("/reset-password?token=", link, StringComparison.Ordinal);
        Assert.Contains("It works once, until", page, StringComparison.Ordinal);

        using var user = Visitor.To(app);
        (await user.ResetPasswordAsync(link, NewPassword)).Dispose();

        Assert.NotNull(await Accounts(app).SignInAsync(email, NewPassword, Visitor.AnyClient, Cancellation));
        Assert.Contains("This link has expired or has already been used.", await user.PageAsync(link), StringComparison.Ordinal);
    }

    /// <summary>Links start with App:PublicBaseUrl when it's set, never with the Host a request names.</summary>
    [Fact]
    public async Task A_link_starts_with_the_public_address()
    {
        await using var setup = await AppWithAdministratorAsync(new() { ["App:PublicBaseUrl"] = AccountApp.PublicBaseUrl });
        var (app, administrator) = (setup.App, setup.Administrator);
        var email = await RegisterAsync(app);

        using var response = await administrator.IssueResetLinkAsync(email);

        Assert.StartsWith(
            $"{AccountApp.PublicBaseUrl}/reset-password?token=",
            LinkIn(await response.Content.ReadAsStringAsync(Cancellation)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issuing_another_link_replaces_the_first()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);
        var email = await RegisterAsync(app);

        using var first = await administrator.IssueResetLinkAsync(email);
        using var second = await administrator.IssueResetLinkAsync(email);
        using var user = Visitor.To(app);

        Assert.Contains(
            "This link has expired or has already been used.",
            await user.PageAsync(LinkIn(await first.Content.ReadAsStringAsync(Cancellation))),
            StringComparison.Ordinal);
        Assert.Contains("Set the password", await user.PageAsync(LinkIn(await second.Content.ReadAsStringAsync(Cancellation))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_address_is_said_to_have_no_account()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);

        using var response = await administrator.IssueResetLinkAsync(Visitor.NewEmail());

        Assert.Contains("No account uses that address.", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>
    /// An administrator's link proves nothing about the inbox. If it verified, a stranger who registered a
    /// listed address could ask for a reset and become an administrator without the bootstrap secret.
    /// </summary>
    [Fact]
    public async Task Using_an_administrators_link_doesnt_verify_the_address()
    {
        var listed = Visitor.NewEmail();
        await using var setup = await AppWithAdministratorAsync(new() { ["Admin:AdminEmails:1"] = listed });
        var (app, administrator) = (setup.App, setup.Administrator);
        await RegisterAsync(app, listed);

        using var response = await administrator.IssueResetLinkAsync(listed);
        using var user = Visitor.To(app);
        (await user.ResetPasswordAsync(LinkIn(await response.Content.ReadAsStringAsync(Cancellation)), NewPassword)).Dispose();

        var userId = await AccountRows.IdAsync(app.ConnectionString(), listed, Cancellation);

        Assert.False(await AccountRows.IsVerifiedAsync(app.ConnectionString(), listed, Cancellation));
        Assert.False(await app.Services.GetRequiredService<IAdministrators>().IsAdministratorAsync(userId, Cancellation));
    }

    [Fact]
    public async Task Issuing_is_audited_as_the_administrators_on_the_users_history()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);
        var email = await RegisterAsync(app);

        using var response = await administrator.IssueResetLinkAsync(email);
        using var user = Visitor.To(app);
        (await user.ResetPasswordAsync(LinkIn(await response.Content.ReadAsStringAsync(Cancellation)), NewPassword)).Dispose();

        var userId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);
        var administratorId = await AccountRows.IdAsync(app.ConnectionString(), setup.AdministratorEmail, Cancellation);
        var rows = await AuditRows.OfUserAsync(app.ConnectionString(), userId, Cancellation);

        Assert.Equal(["PasswordResetLinkIssued", "PasswordReset"], rows.Select(row => row.Action));
        Assert.Equal(("Administrator", (Guid?)administratorId), (rows[0].ActorKind, rows[0].ActorId));
        Assert.Equal(0, await AuditRows.CountHoldingAsync(app.ConnectionString(), CapturedEmail.TokenIn(LinkIn(await response.Content.ReadAsStringAsync(Cancellation))), Cancellation));
    }

    [Fact]
    public async Task The_account_page_links_an_administrator_to_it()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);
        using var user = Visitor.To(app);
        await user.SignUpAsync();

        Assert.Contains($"href=\"{Page}\"", await administrator.PageAsync("/account"), StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"{Page}\"", await user.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anyone_else_is_refused_by_the_page_and_the_contract()
    {
        await using var setup = await AppWithAdministratorAsync();
        var app = setup.App;
        using var user = Visitor.To(app);
        var email = await user.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);

        using var page = await user.GetAsync(Page);
        var result = await Resets(app).IssueAsync(userId, email, Cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(ResetLinkOutcome.NotAllowed, result.Outcome);
        Assert.Null(result.Link);
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in()
    {
        await using var setup = await AppWithAdministratorAsync();
        var app = setup.App;
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync(Page);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(response), StringComparison.Ordinal);
    }

    /// <summary>Signed in as the administrator, because anyone else is turned away before the token is looked at.</summary>
    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused()
    {
        await using var setup = await AppWithAdministratorAsync();
        var (app, administrator) = (setup.App, setup.Administrator);
        var email = await RegisterAsync(app);
        using var form = new FormUrlEncodedContent([new("Input.Email", email)]);

        using var response = await administrator.Client.PostAsync(Page, form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await TokenRows.CountAsync(app.ConnectionString(), email, TokenRows.Reset, Cancellation));
    }

    /// <summary>With email on, users ask for their own, so the tool says so and the contract refuses.</summary>
    [Fact]
    public async Task With_email_on_administrators_dont_issue_links()
    {
        var email = Visitor.NewEmail();
        var captured = new CapturedEmail();
        await using var app = AccountApp.CreateWithEmail(postgres, captured, new() { ["Admin:AdminEmails:0"] = email });
        using var administrator = Visitor.To(app);
        await administrator.SignUpAsync(email);
        (await administrator.VerifyEmailAsync(captured.LastPathTo(email))).Dispose();
        var administratorId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);
        var target = await RegisterAsync(app);

        var page = await administrator.PageAsync(Page);
        var result = await Resets(app).IssueAsync(administratorId, target, Cancellation);

        Assert.Contains("users reset their own passwords", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Issue a link", page, StringComparison.Ordinal);
        Assert.Equal(ResetLinkOutcome.NotAllowed, result.Outcome);
        Assert.DoesNotContain($"href=\"{Page}\"", await administrator.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>The link is as good as a password, so the result never prints it.</summary>
    [Fact]
    public void The_result_doesnt_print_its_link()
    {
        var result = new ResetLinkResult { Outcome = ResetLinkOutcome.Issued, Link = "/reset-password?token=canary1234" };

        Assert.DoesNotContain("canary1234", result.ToString(), StringComparison.Ordinal);
    }

    private static IAccounts Accounts(AppFactory app) => app.Services.GetRequiredService<IAccounts>();

    private static IPasswordResets Resets(AppFactory app) => app.Services.GetRequiredService<IPasswordResets>();

    /// <summary>The link shown on the page, decoded, as a path and query, or an absolute URL when App:PublicBaseUrl is set.</summary>
    private static string LinkIn(string page) => WebUtility.HtmlDecode(Links().Match(page).Groups["link"].Value);

    private static async Task<string> RegisterAsync(AppFactory app, string? email = null)
    {
        using var visitor = Visitor.To(app);
        email ??= Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        return email;
    }

    [GeneratedRegex("id=\"reset-link\"[^>]*value=\"(?<link>[^\"]*)\"")]
    private static partial Regex Links();

    /// <summary>Email off, and an administrator confirmed with the bootstrap secret and signed in.</summary>
    private async Task<WithAdministrator> AppWithAdministratorAsync(Dictionary<string, string?>? settings = null)
    {
        var email = Visitor.NewEmail();
        var all = new Dictionary<string, string?> { ["Admin:AdminEmails:0"] = email, ["Admin:BootstrapSecret"] = Secret };

        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        var app = AccountApp.Create(postgres, all);
        var administrator = Visitor.To(app);
        await administrator.SignUpAsync(email);
        (await administrator.SubmitAsync("/account/administrator", Visitor.ConfirmForm, ("Input.Secret", Secret))).Dispose();

        return new WithAdministrator(app, administrator, email);
    }

    private sealed class WithAdministrator(AppFactory app, Visitor administrator, string administratorEmail) : IAsyncDisposable
    {
        public AppFactory App => app;

        public Visitor Administrator => administrator;

        public string AdministratorEmail => administratorEmail;

        public async ValueTask DisposeAsync()
        {
            administrator.Dispose();
            await app.DisposeAsync();
        }
    }
}
