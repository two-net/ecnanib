using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// A browser, in the ways that matter here: it keeps cookies, reads a form before posting it, and sends
/// back the form's hidden fields, so that antiforgery runs for real. Redirects are left for tests to see.
/// </summary>
internal sealed partial class Visitor(HttpClient client) : IDisposable
{
    public const string Password = "correct horse battery staple";

    public const string SignInForm = "sign-in";

    public const string RegisterForm = "register";

    public const string AcceptForm = "accept-documents";

    public const string ConfirmForm = "confirm-administrator";

    public const string PasswordForm = "change-password";

    private const string SessionCookieName = "ecnanib.session";

    /// <summary>For a test that calls the contract and doesn't care who's asking.</summary>
    public static readonly ClientDetails AnyClient = new(null, null);

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    /// <summary>Where a redirect goes on this site. The framework writes some absolute and some relative.</summary>
    public static string? Target(HttpResponseMessage response)
    {
        var location = response.Headers.Location;

        return location is { IsAbsoluteUri: true } ? location.PathAndQuery : location?.OriginalString;
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public HttpClient Client => client;

    /// <summary>The session cookie from the last sign-in, as a Cookie header would carry it.</summary>
    public string? SessionCookie { get; private set; }

    public static Visitor To(AppFactory app) =>
        new(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

    public static Visitor ToPublic(AppFactory app) => new(app.CreatePublicClient());

    /// <summary>
    /// An application on Kestrel. Not the factory's client, whose option doesn't reach a real socket:
    /// it would follow the redirect after signing in, and lose the cookie that came with it.
    /// </summary>
    public static Visitor ToAddress(Uri address) =>
        new(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = address });

    public Task<HttpResponseMessage> GetAsync(string path) => client.GetAsync(path, Cancellation);

    public async Task<string> PageAsync(string path)
    {
        using var response = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(Cancellation);
    }

    /// <summary>Fills in the form the way a browser would. A field given here replaces a hidden one.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string page, string form, params (string Name, string Value)[] fields)
    {
        var values = HiddenFields(await PageAsync(page), form);

        foreach (var (name, value) in fields)
        {
            values[name] = value;
        }

        using var content = new FormUrlEncodedContent(values);

        return await client.PostAsync(page, content, Cancellation);
    }

    public Task<HttpResponseMessage> RegisterAsync(string email, string password = Password, params (string Name, string Value)[] fields) =>
        SubmitAsync(
            "/register",
            RegisterForm,
            [
                ("Input.Email", email),
                ("Input.Password", password),
                ("Input.ConfirmPassword", password),
                ("Input.AcceptTermsOfUse", "true"),
                ("Input.AcceptRiskDisclosure", "true"),
                .. fields,
            ]);

    public async Task<HttpResponseMessage> SignInAsync(string email, string password = Password, string page = "/sign-in")
    {
        var response = await SubmitAsync(page, SignInForm, ("Input.Email", email), ("Input.Password", password));

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            SessionCookie = cookies
                .Where(cookie => cookie.StartsWith(SessionCookieName + "=", StringComparison.Ordinal))
                .Select(cookie => cookie.Split(';')[0])
                .LastOrDefault() ?? SessionCookie;
        }

        return response;
    }

    public Task<HttpResponseMessage> ChangePasswordAsync(string current, string next, string? confirmation = null) =>
        SubmitAsync(
            "/account/password",
            PasswordForm,
            ("Input.CurrentPassword", current),
            ("Input.NewPassword", next),
            ("Input.ConfirmPassword", confirmation ?? next));

    /// <summary>Registers and signs in, leaving the session in this visitor's cookies.</summary>
    public async Task<string> SignUpAsync(string? email = null)
    {
        email ??= NewEmail();

        (await RegisterAsync(email)).Dispose();
        using var response = await SignInAsync(email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        return email;
    }

    /// <summary>Posts the menu's form from a page, as someone using that page would.</summary>
    public async Task<HttpResponseMessage> SignOutAsync(string page = "/account") =>
        await PostSignOutAsync(await SignOutFormAsync(page));

    /// <summary>What a page holds for its sign-out button, for a test that needs it to go stale first.</summary>
    public async Task<Dictionary<string, string>> SignOutFormAsync(string page = "/account") =>
        HiddenFields(await PageAsync(page), form: null);

    public async Task<HttpResponseMessage> PostSignOutAsync(Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);

        return await client.PostAsync("/sign-out", content, Cancellation);
    }

    public void Dispose() => client.Dispose();

    /// <summary>The hidden fields of a named form, or of the menu's sign-out form, which has no name.</summary>
    private static Dictionary<string, string> HiddenFields(string html, string? form)
    {
        var body = Forms().Matches(html)
            .Select(match => match.Value)
            .Single(candidate => form is null ? candidate.Contains("action=\"/sign-out\"", StringComparison.Ordinal) : Has(candidate, form));

        return Hidden().Matches(body).ToDictionary(
            match => WebUtility.HtmlDecode(match.Groups["name"].Value),
            match => WebUtility.HtmlDecode(match.Groups["value"].Value));
    }

    private static bool Has(string form, string name) =>
        form.Contains($"name=\"_handler\" value=\"{name}\"", StringComparison.Ordinal);

    [GeneratedRegex("<form\\b.*?</form>", RegexOptions.Singleline)]
    private static partial Regex Forms();

    [GeneratedRegex("""<input\b[^>]*type="hidden"[^>]*name="(?<name>[^"]*)"[^>]*value="(?<value>[^"]*)"[^>]*>""")]
    private static partial Regex Hidden();
}

/// <summary>What the tests read and change in the sessions table.</summary>
internal static class SessionRows
{
    /// <summary>A user's sessions, oldest first, so a test can tell them apart by sign-in order.</summary>
    public static async Task<List<Guid>> OfAsync(string connectionString, string email, CancellationToken cancellationToken)
    {
        var id = await AccountRows.IdAsync(connectionString, email, cancellationToken);
        var rows = await Sql.ColumnAsync(connectionString, $"SELECT id::text FROM sessions WHERE user_id = '{id}' ORDER BY created_at", cancellationToken);

        return [.. rows.Select(Guid.Parse)];
    }

    public static Task SetLastSeenAsync(string connectionString, Guid sessionId, string age, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connectionString, $"UPDATE sessions SET last_seen_at = now() - interval '{age}' WHERE id = '{sessionId}'", cancellationToken);

    public static Task SetExpiredAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connectionString, $"UPDATE sessions SET expires_at = now() - interval '1 second' WHERE id = '{sessionId}'", cancellationToken);

    /// <summary>Whether a session was seen just now. The server's clock isn't this host's, so it can read as slightly ahead.</summary>
    public static async Task<bool> WasJustSeenAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken) =>
        Math.Abs(await SecondsSinceSeenAsync(connectionString, sessionId, cancellationToken)) < 10;

    public static Task<double> SecondsSinceSeenAsync(string connectionString, Guid sessionId, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<double>(connectionString, $"SELECT extract(epoch FROM now() - last_seen_at)::float8 FROM sessions WHERE id = '{sessionId}'", cancellationToken);
}

/// <summary>What the tests read and change in a database, by address.</summary>
internal static class AccountRows
{
    public static Task<Guid> IdAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<Guid>(connectionString, $"SELECT id FROM users WHERE normalized_email = '{email.ToLowerInvariant()}'", cancellationToken);

    public static Task<long> CountAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM users WHERE normalized_email = '{email.ToLowerInvariant()}'", cancellationToken);
}
