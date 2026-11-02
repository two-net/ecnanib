using System.Net;
using System.Security.Claims;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// FR-AUTH-10, and the decision on #9: with email off, a listed address is only an administrator once
/// someone proves they hold the configuration file's secret.
/// </summary>
public sealed class AdministratorTests(PostgresFixture postgres)
{
    private const string Secret = "a bootstrap secret of plenty of characters";
    private const string Page = "/account/administrator";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The risk in the issue: anyone could register the address before the operator does.</summary>
    [Fact]
    public async Task Registering_a_listed_address_doesnt_make_an_administrator()
    {
        var email = Visitor.NewEmail();
        await using var app = App(email);
        using var visitor = Visitor.To(app);

        await visitor.SignUpAsync(email);

        Assert.False(await IsAdministratorAsync(app, email));
        Assert.DoesNotContain(">Administrator<", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_secret_makes_a_listed_user_an_administrator()
    {
        var email = Visitor.NewEmail();
        await using var app = App(email);
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);

        Assert.Contains(Page, await visitor.PageAsync("/account"), StringComparison.Ordinal);

        using var response = await ConfirmAsync(visitor, Secret);
        var account = await visitor.PageAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", Visitor.Target(response));
        Assert.True(await IsAdministratorAsync(app, email));
        Assert.Contains(">Administrator<", account, StringComparison.Ordinal);
        Assert.DoesNotContain(Page, account, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wrong_secret_changes_nothing_and_isnt_written_back()
    {
        const string Canary = "canary-secret-1234";

        var email = Visitor.NewEmail();
        await using var app = App(email);
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);

        using var response = await ConfirmAsync(visitor, Canary);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not correct", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, page, StringComparison.Ordinal);
        Assert.False(await IsAdministratorAsync(app, email));
    }

    [Fact]
    public async Task A_listed_address_matches_whatever_its_case()
    {
        var email = Visitor.NewEmail();
        await using var app = App(email.ToUpperInvariant());
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);

        (await ConfirmAsync(visitor, Secret)).Dispose();

        Assert.True(await IsAdministratorAsync(app, email));
    }

    /// <summary>Knowing the secret isn't enough, because rights go to listed addresses.</summary>
    [Fact]
    public async Task The_secret_does_nothing_for_an_address_that_isnt_listed()
    {
        await using var app = App(Visitor.NewEmail());
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);

        using var page = await visitor.GetAsync(Page);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/account", Visitor.Target(page));
        Assert.False(await Administrators(app).ConfirmAsync(id, Secret, Cancellation));
        Assert.False(await IsAdministratorAsync(app, email));
    }

    /// <summary>Signed in, because a visitor is turned away before the token is looked at.</summary>
    [Fact]
    public async Task A_post_without_a_token_is_refused()
    {
        var email = Visitor.NewEmail();
        await using var app = App(email);
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);
        using var form = new FormUrlEncodedContent([new("Input.Secret", Secret)]);

        using var response = await visitor.Client.PostAsync(Page, form, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await IsAdministratorAsync(app, email));
    }

    [Fact]
    public async Task A_visitor_cant_reach_the_page()
    {
        await using var app = App(Visitor.NewEmail());
        using var visitor = Visitor.To(app);

        using var response = await visitor.GetAsync(Page);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(response), StringComparison.Ordinal);
    }

    /// <summary>A flag in the cookie or the table would outlive this, and the operator would be none the wiser.</summary>
    [Fact]
    public async Task Removing_an_address_and_restarting_ends_the_rights()
    {
        var email = Visitor.NewEmail();
        var connectionString = postgres.NewConnectionString();

        await using (var listed = App(email, connectionString))
        {
            using var visitor = Visitor.To(listed);
            await visitor.SignUpAsync(email);
            (await ConfirmAsync(visitor, Secret)).Dispose();

            Assert.True(await IsAdministratorAsync(listed, email));
        }

        await using var removed = AccountApp.Create(postgres, connectionString: connectionString);

        removed.Start();

        Assert.False(await IsAdministratorAsync(removed, email));
    }

    /// <summary>Verification belongs to the emailed link (#13) when there is email, and that fails closed until then.</summary>
    [Fact]
    public async Task With_email_on_the_secret_verifies_nothing()
    {
        var email = Visitor.NewEmail();
        await using var app = AccountApp.Create(
            postgres,
            new()
            {
                ["Admin:AdminEmails:0"] = email,
                ["Admin:BootstrapSecret"] = Secret,
                ["Email:Enabled"] = "true",
            });
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);
        var id = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);

        Assert.False(await Administrators(app).CanConfirmAsync(id, Cancellation));
        Assert.False(await Administrators(app).ConfirmAsync(id, Secret, Cancellation));
        Assert.False(await IsAdministratorAsync(app, email));
    }

    [Fact]
    public async Task The_policy_follows_the_same_rule()
    {
        var email = Visitor.NewEmail();
        await using var app = App(email);
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync(email);

        var id = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test"));
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();

        Assert.False((await authorization.AuthorizeAsync(user, AccountPolicies.Administrator)).Succeeded);

        (await ConfirmAsync(visitor, Secret)).Dispose();

        Assert.True((await authorization.AuthorizeAsync(user, AccountPolicies.Administrator)).Succeeded);
    }

    private static IAdministrators Administrators(AppFactory app) => app.Services.GetRequiredService<IAdministrators>();

    private static Task<HttpResponseMessage> ConfirmAsync(Visitor visitor, string secret) =>
        visitor.SubmitAsync(Page, Visitor.ConfirmForm, ("Input.Secret", secret));

    private static async Task<bool> IsAdministratorAsync(AppFactory app, string email) =>
        await Administrators(app).IsAdministratorAsync(await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation), Cancellation);

    private AppFactory App(string email, string? connectionString = null) =>
        AccountApp.Create(
            postgres,
            new() { ["Admin:AdminEmails:0"] = email, ["Admin:BootstrapSecret"] = Secret },
            connectionString: connectionString);
}
