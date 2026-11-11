using Ecnanib.Accounts;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The pages a user resets a forgotten password with: the form that asks for a link, and the
/// page that the link opens (FR-ACC-2).
/// </summary>
public class PasswordResetPageTests
{
    private const string Ada = "ada@example.com";

    private const string NewPassword = "NEW-MARKER a brand new password";

    [Fact]
    public async Task A_user_who_forgot_their_password_chooses_a_new_one_with_the_link_and_signs_in_with_it()
    {
        // With the host's own events about each request turned on, which name its address.
        using var application = new AccountsApplication(
            settings: file => file.WithJson("Logging:Overrides", """{ "Microsoft.AspNetCore": "Information" }"""));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        // The sign-in page leads to the form.
        Assert.Contains("href=\"/forgot-password\"", await client.PageAsync(SessionAuthentication.SignInPath));

        var (path, sent) = await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));

        Assert.Equal("/forgot-password/sent", path);
        Assert.Contains("<h1>Check your email</h1>", sent);
        Assert.Contains("The link works for 60 minutes.", sent);

        // Opening the link shows the form, on a page that does not pass its address on.
        var message = application.Email.Sent[^1];
        var link = AccountsApplication.LinkIn(message);
        var opened = await client.PageAsync(link.PathAndQuery);
        Assert.Contains("<h1>Choose a new password</h1>", opened);
        Assert.Contains("name=\"Password\"", opened);
        Assert.Contains("autocomplete=\"new-password\"", opened);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\"", opened);

        var (done, changed) = await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", NewPassword));

        // A page of its own, without the token in its address, that leads to the sign-in page.
        Assert.Equal("/reset-password/done", done);
        Assert.Contains("<h1>Your password was changed</h1>", changed);
        Assert.Contains($"href=\"{SessionAuthentication.SignInPath}\"", changed);
        Assert.Empty(StoredSession.All(application.Database));

        // The earlier password signs nobody in, and the new one does.
        Assert.Equal(SessionAuthentication.SignInPath, (await client.SignInAsync(Ada)).Path);
        Assert.Equal("/", (await client.SignInAsync(Ada, NewPassword)).Path);

        // The link is spent.
        var (_, again) = await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", "yet another password"));
        Assert.Contains("This link does not work any more.", again);

        // The requests are in the log, the ones with the link included, with its token masked.
        var log = application.LogText();
        Assert.Contains("/reset-password", log);
        Assert.Contains("token=***", log);
        Assert.DoesNotContain(AccountsApplication.TokenIn(message), log);
        Assert.DoesNotContain("NEW-MARKER", log);
    }

    [Fact]
    public async Task The_form_answers_an_address_without_an_account_as_it_answers_one_that_has_one()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);

        var known = await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));
        var unknown = await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", "grace@example.com"));

        Assert.Equal("/forgot-password/sent", known.Path);
        Assert.Equal(unknown.Path, known.Path);
        Assert.Equal(Browser.AsSeen(unknown.Page), Browser.AsSeen(known.Page));
    }

    [Fact]
    public async Task The_form_says_so_when_what_was_entered_is_not_an_address()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var (path, page) = await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", "ada@nowhere"));

        Assert.Equal("/forgot-password", path);
        Assert.Contains("Enter an email address, such as name@example.com.", page);
        Assert.Contains("value=\"ada@nowhere\"", page);
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task The_form_says_so_when_the_email_could_not_be_sent()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        application.Email.Failure = new InvalidOperationException("The mail server is away.");

        var (path, page) = await client.SubmitAsync("/forgot-password", "forgot-password", ("Email", Ada));

        Assert.Equal("/forgot-password", path);
        Assert.Contains("We could not send the email. Try again in a few minutes.", page);
    }

    [Fact]
    public async Task The_page_says_what_is_wrong_with_a_password_and_does_not_show_it_again()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var before = await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var link = AccountsApplication.LinkIn(application.Email.Sent[^1]);

        var (path, page) = await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", "SHORT-MARKER"[..11]));

        Assert.Equal(PasswordResetLink.Path, path);
        Assert.Contains("Choose a password of at least 12 characters.", page);
        Assert.DoesNotContain("SHORT-MARKE", page);
        Assert.Equal(before.PasswordHash, StoredUser.Of(application.Database, Ada)!.PasswordHash);

        // The link still works.
        var (done, _) = await client.SubmitAsync(link.PathAndQuery, "reset-password", ("Password", NewPassword));
        Assert.Equal("/reset-password/done", done);
    }

    [Theory]
    [InlineData("/reset-password")]
    [InlineData("/reset-password?token=")]
    public async Task A_link_without_a_token_is_said_not_to_work(string path)
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync(path);

        Assert.Contains("This link does not work any more.", page);
        Assert.Contains("href=\"/forgot-password\"", page);
        Assert.DoesNotContain("<form", page);
    }

    [Fact]
    public async Task A_link_with_a_token_that_is_no_accounts_is_said_not_to_work_once_a_password_is_sent()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var before = await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);

        var (_, page) = await client.SubmitAsync(
            "/reset-password?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "reset-password", ("Password", NewPassword));

        Assert.Contains("This link does not work any more.", page);
        Assert.Equal(before.PasswordHash, StoredUser.Of(application.Database, Ada)!.PasswordHash);
    }

    [Fact]
    public async Task A_post_without_the_token_against_request_forgery_sets_no_password()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();
        var before = await application.RegisterVerifiedAsync(Ada);
        await application.RequestResetAsync(Ada);
        var link = AccountsApplication.LinkIn(application.Email.Sent[^1]);

        await client.PostAsync(link.PathAndQuery, "reset-password", "not-the-token", ("Password", NewPassword));

        Assert.Equal(before.PasswordHash, StoredUser.Of(application.Database, Ada)!.PasswordHash);
        Assert.NotNull(StoredUser.Of(application.Database, Ada)!.ResetTokenHash);
    }
}
