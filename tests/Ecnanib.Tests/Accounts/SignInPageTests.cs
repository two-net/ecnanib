using System.Net;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user can sign in with their email address and password, and can sign out (FR-ACC-2). The
/// answer to a sign-in that fails does not tell whether the address has an account.
/// </summary>
public class SignInPageTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_user_signs_in_with_their_address_and_password_and_signs_out()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);

        // A visitor is offered the form.
        var start = await client.PageAsync("/");
        Assert.Contains($"href=\"{SessionAuthentication.SignInPath}\"", start);
        Assert.DoesNotContain("Sign out", start);

        var (path, page) = await client.SignInAsync(Ada);

        // The start page, which now names the user and has the button that signs out.
        Assert.Equal("/", path);
        Assert.Contains(Ada, page);
        Assert.Contains("Sign out", page);
        Assert.DoesNotContain($"href=\"{SessionAuthentication.SignInPath}\"", page);
        Assert.Equal(user.Id, Assert.Single(StoredSession.All(application.Database)).UserId);
        Assert.Contains("You are signed in as ada@example.com.", await client.PageAsync("/account"));

        var (after, signedOut) = await client.SignOutAsync();

        Assert.Equal("/", after);
        Assert.Contains($"href=\"{SessionAuthentication.SignInPath}\"", signedOut);
        Assert.DoesNotContain("Sign out", signedOut);
        Assert.DoesNotContain(Ada, signedOut);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.StartsWith(SessionAuthentication.SignInPath, (await client.OpenAsync("/account")).Address);

        // The log names the account and not the address.
        Assert.Equal(
            user.Id.ToString(),
            Assert.Single(application.Events(), written => written.Template == "User {UserId} signed in").Text("UserId"));
        Assert.Equal(
            user.Id.ToString(),
            Assert.Single(application.Events(), written => written.Template == "User {UserId} signed out").Text("UserId"));
        Assert.DoesNotContain(Ada, application.LogText());
    }

    [Fact]
    public async Task The_address_is_the_same_however_it_is_typed()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var (path, page) = await client.SignInAsync("  Ada@Example.COM ");

        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);
    }

    [Fact]
    public async Task A_wrong_password_and_an_address_without_an_account_get_the_same_answer()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var wrong = await client.SignInAsync(Ada, "WRONG-MARKER is not the password");
        var unknown = await client.SignInAsync("grace@example.com", "WRONG-MARKER is not the password");

        Assert.Equal(SessionAuthentication.SignInPath, wrong.Path);
        Assert.Contains("The email address or the password is not right.", wrong.Page);

        // The same page, but for the address, which is still in its field. The password is not.
        Assert.Contains($"value=\"{Ada}\"", wrong.Page);
        Assert.DoesNotContain("WRONG-MARKER", wrong.Page);
        Assert.Equal(unknown.Path, wrong.Path);
        Assert.Equal(Browser.AsSeen(unknown.Page).Replace("grace@example.com", Ada), Browser.AsSeen(wrong.Page));

        // Nobody is signed in, and the log tells the two apart no more than the page does.
        Assert.Empty(StoredSession.All(application.Database));
        Assert.DoesNotContain("Sign out", await client.PageAsync("/"));
        Assert.Equal(
            2,
            application.Events().Count(written => written.Template == "A sign-in failed: the address or the password was not right"));
        Assert.DoesNotContain("example.com", application.LogText());
        Assert.DoesNotContain("WRONG-MARKER", application.LogText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("correct horse battery")]
    [InlineData("correct horse battery staple ")]
    [InlineData("CORRECT HORSE BATTERY STAPLE")]
    public async Task What_is_not_the_password_signs_nobody_in(string password)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // Posted as it is: a browser would not send a form without a password.
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath)),
            ("Email", Ada),
            ("Password", password));

        // The form again, with its one answer, and not an error.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(SessionAuthentication.SignInPath, answer.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains(
            "The email address or the password is not right.",
            await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ada")]
    [InlineData("ada@example")]
    [InlineData("ada lovelace@example.com")]
    [InlineData("' OR 1=1 --")]
    public async Task What_is_not_an_address_signs_nobody_in(string email)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInPath)),
            ("Email", email),
            ("Password", AccountsApplication.Password));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(
            "The email address or the password is not right.",
            await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task A_password_longer_than_any_account_has_is_refused_without_being_hashed()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var (path, page) = await client.SignInAsync(Ada, new string('x', PasswordRules.MaximumLength + 1));

        Assert.Equal(SessionAuthentication.SignInPath, path);
        Assert.Contains("The email address or the password is not right.", page);
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task An_account_whose_address_is_not_verified_can_sign_in()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterAsync(Ada);
        Assert.Null(StoredUser.Of(application.Database, Ada)!.EmailVerifiedAt);

        var (path, page) = await client.SignInAsync(Ada);

        // Verifying the address is what creating an agent asks for, not what signing in asks for.
        Assert.Equal("/", path);
        Assert.Contains("Sign out", page);
    }

    [Fact]
    public async Task A_visitor_who_opens_a_page_of_signed_in_users_is_sent_to_sign_in_and_back()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var (asked, form) = await client.OpenAsync("/account?tab=security");

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount%3Ftab%3Dsecurity", asked);
        Assert.Contains("<h1>Sign in</h1>", form);
        Assert.DoesNotContain("You are signed in", form);

        using var answer = await client.SendFormAsync(
            asked, "sign-in", Browser.AntiforgeryTokenIn(form), ("Email", Ada), ("Password", AccountsApplication.Password));

        Assert.Equal("/account?tab=security", answer.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.Contains(
            "You are signed in as ada@example.com.",
            await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Someone who is signed in has no use for the form, and is sent on in the same way.
        Assert.Equal("/account", (await client.OpenAsync($"{SessionAuthentication.SignInPath}?returnUrl=%2Faccount")).Address);
        Assert.Equal("/", (await client.OpenAsync(SessionAuthentication.SignInPath)).Address);
    }

    [Theory]
    [InlineData("https://evil.example/account")]
    [InlineData("//evil.example/account")]
    [InlineData("/\\evil.example/account")]
    [InlineData("\\\\evil.example\\account")]
    [InlineData("evil.example")]
    public async Task Signing_in_does_not_lead_to_another_site(string returnUrl)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        var form = $"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var answer = await client.SendFormAsync(
            form,
            "sign-in",
            Browser.AntiforgeryTokenIn(await client.PageAsync(form)),
            ("Email", Ada),
            ("Password", AccountsApplication.Password));

        // Signed in, and at the start page of the application.
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(new Uri("https://localhost/"), answer.RequestMessage!.RequestUri);
        Assert.Single(StoredSession.All(application.Database));

        // The same for someone who is signed in already and opens the link.
        using var opened = await client.GetAsync(form, TestContext.Current.CancellationToken);
        Assert.Equal(new Uri("https://localhost/"), opened.RequestMessage!.RequestUri);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/account", "/account")]
    [InlineData("/account?tab=security&x=%2F%2Fevil.example", "/account?tab=security&x=%2F%2Fevil.example")]
    [InlineData("/a//b", "/a//b")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("account", "/")]
    [InlineData("~/account", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("\\/evil.example", "/")]
    [InlineData("\\\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("https:evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData(" /account", "/")]
    [InlineData("/account\r\nSet-Cookie: x=y", "/")]
    [InlineData("/\t/evil.example", "/")]
    public void Only_a_path_of_the_application_is_somewhere_to_go_after_signing_in(string? returnUrl, string destination)
    {
        Assert.Equal(destination, SessionAuthentication.LocalPathOrHome(returnUrl));
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_signs_nobody_in()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        await client.PostAsync(
            SessionAuthentication.SignInPath,
            "sign-in",
            "not-the-token",
            ("Email", Ada),
            ("Password", AccountsApplication.Password));

        Assert.Empty(StoredSession.All(application.Database));
        Assert.DoesNotContain("Sign out", await client.PageAsync("/"));
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_signs_nobody_out()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.SendFormAsync(WebUi.WebUiModule.SignOutPath, "sign-out", "not-the-token");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Single(StoredSession.All(application.Database));
        Assert.Contains("You are signed in as ada@example.com.", await client.PageAsync("/account"));
    }

    [Fact]
    public async Task The_button_on_a_page_that_is_older_than_its_session_leads_to_the_start_page()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The page is left open until the session has ended. Its token against request forgery
        // was made for the user, who the request no longer is.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync("/account"));
        clock.Set(Morning.AddHours(2));

        using var answer = await client.SendFormAsync(WebUi.WebUiModule.SignOutPath, "sign-out", token);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("/", answer.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.DoesNotContain("Sign out", await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(StoredSession.All(application.Database));
    }

    [Fact]
    public async Task Signing_out_without_being_signed_in_changes_nothing()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        using var other = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await other.SignInAsync(Ada);

        var (path, _) = await client.PostAsync(WebUi.WebUiModule.SignOutPath, "sign-out", "no-token-at-all");

        Assert.Equal("/", path);
        Assert.Single(StoredSession.All(application.Database));
    }
}
