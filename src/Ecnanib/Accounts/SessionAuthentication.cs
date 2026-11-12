using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts;

/// <summary>
/// How a request is known to be signed in: by the session its cookie names (FR-ACC-2). See
/// docs/accounts.md.
/// </summary>
public static class SessionAuthentication
{
    /// <summary>The name of the authentication scheme, which is the application's only one.</summary>
    public const string Scheme = "Session";

    /// <summary>The path of the page a visitor signs in on.</summary>
    public const string SignInPath = "/sign-in";

    /// <summary>
    /// The path of the page that takes the code of a user with two-factor authentication, after
    /// the sign-in page took their password (FR-ACC-3).
    /// </summary>
    public const string SignInCodePath = "/sign-in/code";

    /// <summary>The query parameter of both pages that says where to go after signing in.</summary>
    public const string ReturnUrlParameter = "returnUrl";

    /// <summary>
    /// The address of one of the two pages, with where to go after signing in carried along when
    /// there is somewhere to go.
    /// </summary>
    /// <param name="path"><see cref="SignInPath"/> or <see cref="SignInCodePath"/>.</param>
    /// <param name="returnUrl">Where to go after signing in, as the page was given it.</param>
    public static string PageWith(string path, string? returnUrl) =>
        string.IsNullOrEmpty(returnUrl) ? path : QueryHelpers.AddQueryString(path, ReturnUrlParameter, returnUrl);

    /// <summary>
    /// Where to go after signing in: <paramref name="returnUrl"/> when it is a path of this
    /// application, and the start page when it is anything else. An address that leads elsewhere
    /// is never followed, so a link to the sign-in page cannot send a user on to another site.
    /// </summary>
    public static string LocalPathOrHome(string? returnUrl)
    {
        // A path starts with one slash. Two, or a slash and a backslash, start another host.
        var local = returnUrl is { Length: > 0 }
            && returnUrl[0] == '/'
            && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            && !returnUrl.Any(char.IsControl);

        return local ? returnUrl! : "/";
    }
}

/// <summary>
/// Reads the session cookie of a request and makes the user of its session the user of the
/// request, with the account's id as the name identifier.
/// </summary>
/// <remarks>
/// Authentication runs ahead of the error handling, so this does not throw. When the session
/// cannot be looked up, the request goes on as not signed in, with its cookie left alone, and a
/// page that needs a signed-in user fails with that error instead of asking the user to sign in.
/// </remarks>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    Sessions sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private Exception? _lookupFailure;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (SessionCookie.TokenOf(Request) is not { } token)
        {
            return AuthenticateResult.NoResult();
        }

        SignedInUser? user;
        try
        {
            user = await sessions.FindAsync(token, Context.RequestAborted);
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            // The browser has gone, and with it whoever would get an answer.
            return AuthenticateResult.NoResult();
        }
        catch (Exception error)
        {
            _lookupFailure = error;

            // The error itself is logged in full, once, if a page turns out to need the user.
            Logger.LogWarning(
                "The session of a request could not be looked up, so the request goes on as not signed in: {Reason}",
                error.Message);

            return AuthenticateResult.NoResult();
        }

        if (user is null)
        {
            // The session has ended, so the browser need not send its token again.
            SessionCookie.Clear(Response);

            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Email),
            ],
            Scheme.Name);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>Sends a visitor who asked for a page of signed-in users to the sign-in page, and from there back.</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await HandleAuthenticateOnceAsync();

        if (_lookupFailure is { } failure)
        {
            // The user may well be signed in. This is inside the error handling, which answers
            // with the error page and logs the error.
            ExceptionDispatchInfo.Throw(failure);
        }

        // A page is asked for again after signing in. A form that was posted is not posted again.
        var page = Request.PathBase.Add(Request.Path).ToString();
        var returnUrl = HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method)
            ? page + Request.QueryString
            : page;

        Response.Redirect(QueryHelpers.AddQueryString(
            SessionAuthentication.SignInPath, SessionAuthentication.ReturnUrlParameter, returnUrl));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
