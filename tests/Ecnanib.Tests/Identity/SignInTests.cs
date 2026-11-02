using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-04, and how credentials reach the server without crossing a circuit.</summary>
public sealed partial class SignInTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private const string SessionCookie = "ecnanib.session";
    private const string Canary = "canary-password-1234";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_in_sets_the_session_and_goes_to_the_account()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();
        using var response = await visitor.SignInAsync(email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", Visitor.Target(response));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(SessionCookie, StringComparison.Ordinal));

        Assert.Contains(email, await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }

    /// <summary>The first interactive render empties a prerendered page, and a circuit would carry the password.</summary>
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/register")]
    public async Task A_page_that_takes_a_password_stays_static(string page)
    {
        using var visitor = Visitor.To(app);

        Assert.DoesNotContain("<!--Blazor:", await visitor.PageAsync(page), StringComparison.Ordinal);
    }

    /// <summary>InputText writes its value into the page, and the framework's own sign-in sample echoes it this way.</summary>
    [Fact]
    public async Task A_refusal_keeps_the_email_and_never_the_password()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.SignInAsync(email, Canary);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("couldn&#x27;t sign you in", page, StringComparison.Ordinal);
        Assert.Contains(email, page, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The framework appends encrypted state to a page, which a search of the HTML can't see into, and
    /// persisting a form model would put the password there. If the purpose string changes with a
    /// framework upgrade, this fails on Unprotect: find the new one in ProtectedPrerenderComponentApplicationStore.
    /// </summary>
    [Fact]
    public async Task The_encrypted_state_on_a_refused_page_doesnt_carry_the_password()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.SignInAsync(Visitor.NewEmail(), Canary);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        var state = PersistedState().Match(page).Groups["state"].Value;
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Components.Server.State");
        var json = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(state)));
        var entries = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(json)!;

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(entry.Value), StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MalformedAddresses.Posted), MemberType = typeof(MalformedAddresses))]
    public async Task A_malformed_address_is_an_ordinary_refusal(string description, string email)
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.SignInAsync(email, Canary);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{description} got {response.StatusCode}");
        Assert.Contains("couldn&#x27;t sign you in", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedAddresses.All), MemberType = typeof(MalformedAddresses))]
    public async Task The_contract_refuses_a_malformed_address_too(string description, string email)
    {
        var accounts = app.Services.GetRequiredService<IAccounts>();

        Assert.True(await accounts.SignInAsync(email, Canary, Visitor.AnyClient, Cancellation) is null, description);
    }

    /// <summary>The form reader refuses NUL, so this never reaches PostgreSQL, which would refuse it with an Error.</summary>
    [Fact]
    public async Task A_NUL_in_a_form_is_refused_before_anything_runs()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.SignInAsync("\0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_form_with_nothing_filled_in_is_an_ordinary_refusal()
    {
        using var visitor = Visitor.To(app);

        using var response = await visitor.SignInAsync(string.Empty, string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("couldn&#x27;t sign you in", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    /// <summary>A visitor writes the query string, and a value that can't be parsed used to be a 500 and an Error row.</summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("1")]
    public async Task A_query_value_nobody_expected_is_ignored(string value)
    {
        using var visitor = Visitor.To(app);

        var page = await visitor.PageAsync($"/sign-in?registered={value}");

        Assert.DoesNotContain("Registration received", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_says_when_someone_has_just_registered()
    {
        using var visitor = Visitor.To(app);

        Assert.Contains("Registration received", await visitor.PageAsync("/sign-in?registered=true"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_signs_in_whatever_its_case_or_surrounding_space()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();
        using var response = await visitor.SignInAsync($"  {email.ToUpperInvariant()} ");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        using var signedOut = await visitor.SignOutAsync();
        using var account = await visitor.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/", Visitor.Target(signedOut));
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task Signing_in_goes_back_to_a_local_return_url()
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();
        using var response = await visitor.SignInAsync(email, page: "/sign-in?returnUrl=%2Faccount%3Ftab%3D1");

        Assert.Equal("/account?tab=1", Visitor.Target(response));
    }

    /// <summary>A return URL is in a link that anyone can write, so this is what an open redirect would look like.</summary>
    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("http:/evil.example")]
    public async Task Signing_in_never_goes_to_another_site(string returnUrl)
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();
        using var response = await visitor.SignInAsync(email, page: $"/sign-in?returnUrl={WebUtility.UrlEncode(returnUrl)}");

        Assert.Equal(new Uri(visitor.Client.BaseAddress!, "/account"), new Uri(visitor.Client.BaseAddress!, response.Headers.Location!));
    }

    [GeneratedRegex("<!--Blazor-Server-Component-State:(?<state>[^-]+)-->")]
    private static partial Regex PersistedState();
}
