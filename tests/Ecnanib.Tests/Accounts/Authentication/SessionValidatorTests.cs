using System.Security.Claims;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Authentication;

/// <summary>FR-ACC-03/04: a session is re-checked against the account after the interval, or at once after a revocation.</summary>
public class SessionValidatorTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task KeepsAPrincipalWhoseStampStillMatches()
    {
        var (validator, stamps, _, time, _) = Build();

        var check = await validator.CheckAsync(Principal(), time.GetUtcNow().AddMinutes(-6), Ct);

        Assert.Equal(SessionCheck.Kept, check);
        Assert.Equal(1, stamps.Calls);
    }

    [Fact]
    public async Task SkipsTheLookupInsideTheRevalidationInterval()
    {
        var (validator, stamps, _, time, _) = Build();

        var check = await validator.CheckAsync(Principal(), time.GetUtcNow().AddMinutes(-4), Ct);

        Assert.Equal(SessionCheck.KeptWithoutLookup, check);
        Assert.Equal(0, stamps.Calls);
    }

    [Fact]
    public async Task ANullIssueTimeForcesALookup()
    {
        var (validator, stamps, _, _, _) = Build();

        Assert.Equal(SessionCheck.Kept, await validator.CheckAsync(Principal(), null, Ct));
        Assert.Equal(1, stamps.Calls);
    }

    [Fact]
    public async Task RejectsWhenTheStampChanged()
    {
        var (validator, stamps, _, time, logs) = Build();
        stamps.Stamp = "stamp-2";

        var check = await validator.CheckAsync(Principal(), time.GetUtcNow().AddMinutes(-6), Ct);

        Assert.Equal(SessionCheck.Rejected, check);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"Session of user {UserId} rejected: security stamp changed");
    }

    [Fact]
    public async Task RejectsWhenTheUserIsGone()
    {
        var (validator, stamps, _, _, logs) = Build();
        stamps.Stamp = null;

        Assert.Equal(SessionCheck.Rejected, await validator.CheckAsync(Principal(), null, Ct));
        Assert.Contains(logs.Entries, e => e.Message == $"Session of user {UserId} rejected: account no longer exists");
    }

    [Fact]
    public async Task RejectsAMalformedPrincipalWithoutALookup()
    {
        var (validator, stamps, _, _, _) = Build();
        var noStamp = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D"))], "Cookies"));

        Assert.Equal(SessionCheck.Rejected, await validator.CheckAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, Ct));
        Assert.Equal(SessionCheck.Rejected, await validator.CheckAsync(noStamp, null, Ct));
        Assert.Equal(SessionCheck.Rejected, await validator.CheckAsync(null, null, Ct));
        Assert.Equal(0, stamps.Calls);
    }

    [Fact]
    public async Task ARevocationForcesAnImmediateLookupForOlderTicketsOnly()
    {
        var (validator, stamps, revocations, time, _) = Build();
        var issuedBefore = time.GetUtcNow().AddSeconds(-1);
        revocations.Revoke(UserId);
        stamps.Stamp = "stamp-2";

        Assert.Equal(SessionCheck.Rejected, await validator.CheckAsync(Principal("stamp-1"), issuedBefore, Ct));
        Assert.Equal(1, stamps.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SessionCheck.KeptWithoutLookup, await validator.CheckAsync(Principal("stamp-2"), time.GetUtcNow(), Ct));
        Assert.Equal(1, stamps.Calls);
    }

    [Fact]
    public void RevocationsExpireWithTheSession()
    {
        var (_, _, revocations, time, _) = Build();
        revocations.Revoke(UserId);
        Assert.Equal(1, revocations.Count);

        time.Advance(TimeSpan.FromDays(31));
        revocations.Revoke(Guid.NewGuid());

        Assert.Equal(1, revocations.Count);
        Assert.False(revocations.IsRevokedSince(UserId, time.GetUtcNow().AddDays(-40)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string stamp = "stamp-1") =>
        AccountSession.CreatePrincipal(new User { Id = UserId, Email = "t@example.com", NormalizedEmail = "T@EXAMPLE.COM", PasswordHash = "", SecurityStamp = stamp });

    private static (SessionValidator Validator, FakeStampSource Stamps, SessionRevocations Revocations, FakeTimeProvider Time, TestLoggerProvider Logs) Build()
    {
        var time = new FakeTimeProvider();
        var logs = new TestLoggerProvider();
        var options = Options.Create(new AccountsOptions());
        var stamps = new FakeStampSource();
        var revocations = new SessionRevocations(options, time);
        var validator = new SessionValidator(stamps, revocations, options, time, TestLogging.CreateFactory(logs).CreateLogger<SessionValidator>());
        return (validator, stamps, revocations, time, logs);
    }

    internal sealed class FakeStampSource : ISecurityStampSource
    {
        public string? Stamp { get; set; } = "stamp-1";

        public int Calls { get; private set; }

        public Task<string?> FindAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Stamp);
        }
    }
}
