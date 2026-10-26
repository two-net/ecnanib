using System.Net;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Database.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// A signed-in session on the <see cref="PipelineTestHost"/>: an account registered through the accounts module (a real
/// Argon2 hash, so the sign-in form accepts it), the form posted as a browser would, and pages fetched with the cookie.
/// </summary>
internal static class PipelineSession
{
    public const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<User> RegisterAsync(PipelineTestHost host, string email)
    {
        var result = await host.Services.GetRequiredService<IAccountService>().RegisterAsync(email, Password, "127.0.0.1", Ct);
        Assert.Equal(RegistrationStatus.Succeeded, result.Status);
        return result.User!;
    }

    /// <summary>Signs in through the form and returns the session cookie pair.</summary>
    public static async Task<string> SignInAsync(PipelineTestHost host, string email)
    {
        var (html, antiforgery) = await PageAsync(host, "/sign-in");
        var response = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Post, "/sign-in", cookies: [antiforgery],
            content: PipelineTestHost.Form(("_handler", "sign-in"), ("__RequestVerificationToken", PipelineTestHost.AntiforgeryToken(html)), ("Input.Email", email), ("Input.Password", Password))), Ct);
        var session = PipelineTestHost.CookiePair(response, "ecnanib.session");
        Assert.NotNull(session);
        return session;
    }

    /// <summary>A page that must answer 200, with the antiforgery cookie its forms need.</summary>
    public static async Task<(string Html, string Antiforgery)> PageAsync(PipelineTestHost host, string path, params string[] cookies)
    {
        var page = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, path, cookies: cookies), Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var antiforgery = PipelineTestHost.CookiePair(page, "ecnanib.antiforgery") ?? cookies.FirstOrDefault(c => c.StartsWith("ecnanib.antiforgery=", StringComparison.Ordinal));
        Assert.NotNull(antiforgery);
        return (await page.Content.ReadAsStringAsync(Ct), antiforgery);
    }

    /// <summary>Any page, whatever it answers, with its body.</summary>
    public static async Task<(HttpResponseMessage Response, string Html)> GetAsync(PipelineTestHost host, string path, string? session = null)
    {
        var response = await host.Client.SendAsync(PipelineTestHost.Request(HttpMethod.Get, path, cookies: session is null ? null : [session]), Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }
}
