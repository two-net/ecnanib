using System.Security.Claims;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts.Authentication;

/// <summary>The cookie handler drops and signs out a rejected session and renews a kept one.</summary>
public class AccountCookieEventsTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task ARejectedPrincipalIsDroppedAndSignedOut()
    {
        var (events, stamps, recorder, time, http) = Build();
        stamps.Stamp = "stamp-2";
        var context = Context(http, time.GetUtcNow().AddMinutes(-6));

        await events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal([CookieAuthenticationDefaults.AuthenticationScheme], recorder.SignOuts);
    }

    [Fact]
    public async Task AKeptPrincipalIsRenewedSoIssuedUtcAdvances()
    {
        var (events, stamps, recorder, time, http) = Build();
        var context = Context(http, time.GetUtcNow().AddMinutes(-6));

        await events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.True(context.ShouldRenew);
        Assert.Equal(1, stamps.Calls);
        Assert.Empty(recorder.SignOuts);
    }

    [Fact]
    public async Task AFreshPrincipalIsLeftAlone()
    {
        var (events, stamps, recorder, time, http) = Build();
        var context = Context(http, time.GetUtcNow().AddMinutes(-1));

        await events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.False(context.ShouldRenew);
        Assert.Equal(0, stamps.Calls);
        Assert.Empty(recorder.SignOuts);
    }

    private static CookieValidatePrincipalContext Context(HttpContext http, DateTimeOffset issuedUtc)
    {
        var principal = AccountSession.CreatePrincipal(new User { Id = UserId, Email = "t@example.com", NormalizedEmail = "T@EXAMPLE.COM", PasswordHash = "", SecurityStamp = "stamp-1" });
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties { IssuedUtc = issuedUtc, IsPersistent = true }, CookieAuthenticationDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        return new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket);
    }

    private static (AccountCookieEvents Events, SessionValidatorTests.FakeStampSource Stamps, RecordingAuthenticationService Recorder, FakeTimeProvider Time, HttpContext Http) Build()
    {
        var time = new FakeTimeProvider();
        var stamps = new SessionValidatorTests.FakeStampSource();
        var recorder = new RecordingAuthenticationService();
        var provider = AccountsTestHost.BuildServices(time: time, configure: s =>
        {
            s.AddSingleton<ISecurityStampSource>(stamps);
            s.AddSingleton<IAuthenticationService>(recorder);
        });
        var scope = provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return (scope.ServiceProvider.GetRequiredService<AccountCookieEvents>(), stamps, recorder, time, http);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<string?> SignOuts { get; } = [];

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOuts.Add(scheme);
            return Task.CompletedTask;
        }
    }
}
