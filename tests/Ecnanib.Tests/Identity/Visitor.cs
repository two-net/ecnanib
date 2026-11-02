using System.Net;
using System.Text.RegularExpressions;
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

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    /// <summary>Where a redirect goes on this site. The framework writes some absolute and some relative.</summary>
    public static string? Target(HttpResponseMessage response)
    {
        var location = response.Headers.Location;

        return location is { IsAbsoluteUri: true } ? location.PathAndQuery : location?.OriginalString;
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public HttpClient Client => client;

    public static Visitor To(AppFactory app) =>
        new(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

    public static Visitor ToPublic(AppFactory app) => new(app.CreatePublicClient());

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

    public Task<HttpResponseMessage> SignInAsync(string email, string password = Password, string page = "/sign-in") =>
        SubmitAsync(page, SignInForm, ("Input.Email", email), ("Input.Password", password));

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
    public async Task<HttpResponseMessage> SignOutAsync(string page = "/account")
    {
        var values = HiddenFields(await PageAsync(page), form: null);

        using var content = new FormUrlEncodedContent(values);

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

/// <summary>What the tests read and change in a database, by address.</summary>
internal static class AccountRows
{
    public static Task<Guid> IdAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<Guid>(connectionString, $"SELECT id FROM users WHERE normalized_email = '{email.ToLowerInvariant()}'", cancellationToken);

    public static Task<long> CountAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM users WHERE normalized_email = '{email.ToLowerInvariant()}'", cancellationToken);
}
