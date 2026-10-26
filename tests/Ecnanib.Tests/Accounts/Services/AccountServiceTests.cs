using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Accounts.Services;

/// <summary>The paths that must never reach the database, and what reaches the logs when the database fails (FR-LOG-05).</summary>
public class AccountServiceTests
{
    private const string Email = "trader@example.com";
    private const string Password = "correct horse battery staple";
    private const string Address = "10.0.0.1";

    private static CancellationToken Ct => AccountsTestHost.Ct;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Bob<bob@x.com>")]
    public async Task RejectsAMalformedEmailBeforeTouchingTheDatabase(string? email)
    {
        var (accounts, factory, _, provider) = Build();
        await using var _ = provider;

        var result = await accounts.RegisterAsync(email, Password, Address, Ct);

        Assert.Equal(RegistrationStatus.InvalidEmail, result.Status);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData("short", null)]
    [InlineData("elevenchars", "12")]
    public async Task RejectsAWeakPasswordBeforeTouchingTheDatabase(string password, string? minLength)
    {
        var (accounts, factory, _, provider) = Build(minLength is null ? null : new() { ["Accounts:MinPasswordLength"] = minLength });
        await using var _ = provider;

        Assert.Equal(RegistrationStatus.InvalidPassword, (await accounts.RegisterAsync(Email, password, Address, Ct)).Status);
        Assert.Equal(PasswordChangeStatus.InvalidNewPassword, (await accounts.ChangePasswordAsync(Guid.NewGuid(), Password, password, Address, Ct)).Status);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task AThrottledSignInNeverReachesTheDatabase()
    {
        var (accounts, factory, logs, provider) = Build();
        await using var _ = provider;
        var throttle = provider.GetRequiredService<SignInThrottle>();
        for (var i = 0; i < 10; i++)
            throttle.TryBegin(Email.ToUpperInvariant(), Address);

        var result = await accounts.AuthenticateAsync(Email, Password, Address, Ct);

        Assert.Equal(AuthenticationStatus.Throttled, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(15), result.RetryAfter);
        Assert.Equal(0, factory.Calls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("throttled", StringComparison.Ordinal));
        AssertNoCredentials(logs);
    }

    [Fact]
    public async Task AThrottledRegistrationIsRefusedByAddress()
    {
        var (accounts, factory, logs, provider) = Build();
        await using var _ = provider;
        var throttle = provider.GetRequiredService<SignInThrottle>();
        for (var i = 0; i < 100; i++)
            throttle.TryBeginForAddress(Address);

        var result = await accounts.RegisterAsync(Email, Password, Address, Ct);

        Assert.Equal(RegistrationStatus.Throttled, result.Status);
        Assert.Equal(0, factory.Calls);
        AssertNoCredentials(logs);
    }

    [Fact]
    public async Task AMalformedEmailSignInFailsWithoutTheDatabase()
    {
        var (accounts, factory, logs, provider) = Build();
        await using var _ = provider;

        var result = await accounts.AuthenticateAsync("nope", Password, Address, Ct);

        Assert.Equal(AuthenticationStatus.InvalidCredentials, result.Status);
        Assert.Equal(0, factory.Calls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Sign-in failed: unknown account");
        Assert.DoesNotContain("nope", logs.AllText);
        AssertNoCredentials(logs);
    }

    [Fact]
    public async Task DatabaseFailuresPropagateWithoutTheCredentials()
    {
        var (accounts, factory, logs, provider) = Build();
        await using var _ = provider;
        var userId = Guid.NewGuid();
        var calls = new Func<Task>[]
        {
            () => accounts.RegisterAsync(Email, Password, Address, Ct),
            () => accounts.AuthenticateAsync(Email, Password, Address, Ct),
            () => accounts.ChangePasswordAsync(userId, Password, Password + "2", Address, Ct),
            () => accounts.DeleteAccountAsync(userId, Password, Address, Ct),
            () => accounts.GetDisclaimerStatusAsync(userId, Ct),
            () => accounts.AcceptDisclaimerAsync(userId, Ct),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.DoesNotContain(Password, ex.ToString());
            Assert.DoesNotContain(Email, ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(calls.Length, factory.Calls);
        AssertNoCredentials(logs);
    }

    private static void AssertNoCredentials(TestLoggerProvider logs)
    {
        Assert.DoesNotContain(Password, logs.AllText);
        Assert.DoesNotContain(Email, logs.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static (IAccountService Accounts, ThrowingDbContextFactory Factory, TestLoggerProvider Logs, ServiceProvider Provider) Build(Dictionary<string, string?>? config = null)
    {
        var factory = new ThrowingDbContextFactory();
        var logs = new TestLoggerProvider();
        var provider = AccountsTestHost.BuildServices(logs: logs, config: config, time: new FakeTimeProvider(), configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));
        return (provider.GetRequiredService<IAccountService>(), factory, logs, provider);
    }
}
