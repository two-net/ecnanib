using System.Security.Cryptography;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Services;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Ecnanib.Ui.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Accounts.Live;

/// <summary>FR-ACC-01..04 and FR-ACC-08 against a real PostgreSQL database.</summary>
[Trait("Category", "Live")]
public class AccountServiceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string Password = "correct horse battery staple";
    private const string Address = "10.0.0.1";
    private readonly DatabaseLiveFixture _fixture;

    public AccountServiceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegistersSignsInAndRefusesADuplicateEmailWhateverItsCase()
    {
        await StartAsync();
        var (provider, logs, _) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();

        var registered = await accounts.RegisterAsync(" Live.Trader@Example.com ", Password, Address, Ct);

        Assert.Equal(RegistrationStatus.Succeeded, registered.Status);
        var user = registered.User!;
        Assert.Equal("Live.Trader@Example.com", user.Email);
        Assert.Equal("LIVE.TRADER@EXAMPLE.COM", user.NormalizedEmail);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", user.PasswordHash);
        Assert.Equal(32, user.SecurityStamp.Length);
        Assert.Equal(LiveSeed.Now, user.CreatedAt);
        Assert.Null(user.DisclaimerAcceptedAt);
        Assert.Equal(7, user.Id.Version);

        Assert.Equal(RegistrationStatus.EmailTaken, (await accounts.RegisterAsync("live.trader@example.COM", Password, Address, Ct)).Status);
        await using (var db = _fixture.CreateContext())
            Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == "LIVE.TRADER@EXAMPLE.COM", Ct));

        var signedIn = await accounts.AuthenticateAsync("LIVE.TRADER@example.com", Password, Address, Ct);
        Assert.Equal(AuthenticationStatus.Succeeded, signedIn.Status);
        Assert.Equal(user.Id, signedIn.User!.Id);
        Assert.Equal(AuthenticationStatus.InvalidCredentials, (await accounts.AuthenticateAsync(user.Email, Password + "!", Address, Ct)).Status);
        Assert.Equal(AuthenticationStatus.InvalidCredentials, (await accounts.AuthenticateAsync("nobody@example.com", Password, Address, Ct)).Status);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"User {user.Id} registered");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"User {user.Id} signed in");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == $"Sign-in failed for user {user.Id}: wrong password");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Sign-in failed: unknown account");
        AssertNoCredentials(logs, "live.trader");

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task ConcurrentRegistrationsOfTheSameEmailYieldOneUserAndThreeRefusals()
    {
        await StartAsync();
        var (provider, logs, _) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        string[] variants = ["race@example.com", "RACE@example.com", "Race@Example.com", "race@EXAMPLE.COM"];

        var results = await Task.WhenAll(variants.Select(v => accounts.RegisterAsync(v, Password, Address, Ct)));

        Assert.Equal(1, results.Count(r => r.Status == RegistrationStatus.Succeeded));
        Assert.Equal(3, results.Count(r => r.Status == RegistrationStatus.EmailTaken));
        // EF Core logs the lost INSERT itself (parameters masked); the module logs the race as a refusal, not an error.
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error && e.Category.StartsWith("Ecnanib.", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Registration refused: email already registered");
        AssertNoCredentials(logs, "race@example.com");
        await using (var db = _fixture.CreateContext())
            Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == "RACE@EXAMPLE.COM", Ct));

        await DeleteUsersAsync(results.Single(r => r.User is not null).User!.Id);
    }

    [Fact]
    public async Task ChangingThePasswordRotatesTheStampAndRetiresTheOldPassword()
    {
        await StartAsync();
        var (provider, logs, time) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        var user = (await accounts.RegisterAsync("change@example.com", Password, Address, Ct)).User!;
        var stamps = provider.GetRequiredService<ISecurityStampSource>();
        var revocations = provider.GetRequiredService<SessionRevocations>();

        Assert.Equal(PasswordChangeStatus.InvalidCurrentPassword, (await accounts.ChangePasswordAsync(user.Id, "wrong password", Password + "2", Address, Ct)).Status);
        Assert.Equal(user.SecurityStamp, await stamps.FindAsync(user.Id, Ct));

        var changed = await accounts.ChangePasswordAsync(user.Id, Password, Password + "2", Address, Ct);

        Assert.Equal(PasswordChangeStatus.Succeeded, changed.Status);
        Assert.NotEqual(user.SecurityStamp, changed.User!.SecurityStamp);
        Assert.Equal(changed.User.SecurityStamp, await stamps.FindAsync(user.Id, Ct));
        Assert.True(revocations.IsRevokedSince(user.Id, time.GetUtcNow()));
        Assert.Equal(AuthenticationStatus.InvalidCredentials, (await accounts.AuthenticateAsync(user.Email, Password, Address, Ct)).Status);
        Assert.Equal(AuthenticationStatus.Succeeded, (await accounts.AuthenticateAsync(user.Email, Password + "2", Address, Ct)).Status);
        Assert.Equal(PasswordChangeStatus.UserNotFound, (await accounts.ChangePasswordAsync(Guid.NewGuid(), Password, Password + "3", Address, Ct)).Status);
        AssertNoCredentials(logs, "change@example.com");

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task RehashesAnOlderHashOnSuccessfulSignIn()
    {
        await StartAsync();
        var (provider, _, _) = Build();
        await using var _ = provider;
        var hasher = provider.GetRequiredService<IPasswordHasher<User>>();
        var salt = RandomNumberGenerator.GetBytes(16);
        var user = LiveSeed.NewUser("rehash@example.com");
        user.PasswordHash = Argon2idPasswordHasher.Format(8192, 1, 1, salt, Argon2idPasswordHasher.Compute(Password, salt, 8192, 1, 1, 32));
        await using (var db = _fixture.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync(Ct);
        }

        var signedIn = await provider.GetRequiredService<IAccountService>().AuthenticateAsync(user.Email, Password, Address, Ct);

        Assert.Equal(AuthenticationStatus.Succeeded, signedIn.Status);
        await using (var db = _fixture.CreateContext())
        {
            var stored = await db.Users.SingleAsync(u => u.Id == user.Id, Ct);
            Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", stored.PasswordHash);
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(stored, stored.PasswordHash, Password));
        }

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task AcceptingTheDisclaimerIsIdempotentAndStampsTheUser()
    {
        await StartAsync();
        var (provider, logs, time) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        var user = LiveSeed.NewUser("disclaimer@example.com");
        user.DisclaimerAcceptedAt = LiveSeed.Now.AddDays(-30);
        await using (var db = _fixture.CreateContext())
        {
            db.Add(user);
            db.Add(new DisclaimerAcceptance { User = user, Version = "2026-01", AcceptedAt = LiveSeed.Now.AddDays(-30) });
            await db.SaveChangesAsync(Ct);
        }

        var before = await accounts.GetDisclaimerStatusAsync(user.Id, Ct);
        Assert.Equal(RiskDisclaimer.Version, before.CurrentVersion);
        Assert.False(before.IsAccepted);
        Assert.Equal(LiveSeed.Now.AddDays(-30), before.LastAcceptedAt);

        var accepted = await accounts.AcceptDisclaimerAsync(user.Id, Ct);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(time.GetUtcNow(), accepted.CurrentVersionAcceptedAt);
        Assert.Equal(time.GetUtcNow(), accepted.LastAcceptedAt);

        var firstAcceptance = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(1));
        var again = await accounts.AcceptDisclaimerAsync(user.Id, Ct);
        Assert.Equal(firstAcceptance, again.CurrentVersionAcceptedAt);
        Assert.Equal(firstAcceptance, again.LastAcceptedAt);

        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(1, await db.DisclaimerAcceptances.CountAsync(a => a.UserId == user.Id && a.Version == RiskDisclaimer.Version, Ct));
            Assert.Equal(2, await db.DisclaimerAcceptances.CountAsync(a => a.UserId == user.Id, Ct));
            Assert.Equal(firstAcceptance, (await db.Users.SingleAsync(u => u.Id == user.Id, Ct)).DisclaimerAcceptedAt);
        }

        Assert.Single(logs.Entries, e => e.Message == $"User {user.Id} accepted risk disclaimer {RiskDisclaimer.Version}");
        Assert.False((await accounts.AcceptDisclaimerAsync(Guid.NewGuid(), Ct)).IsAccepted);

        await DeleteUsersAsync(user.Id);
    }

    [Theory]
    [InlineData(AgentState.Running)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Error)]
    public async Task DeletionIsRefusedWhileAnAgentIsRunningPausedOrInError(AgentState state)
    {
        await StartAsync();
        var (provider, _, _) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        var user = (await accounts.RegisterAsync($"blocked-{state}@example.com", Password, Address, Ct)).User!;
        await using (var db = _fixture.CreateContext())
        {
            var active = LiveSeed.NewAgent(user, "active");
            active.State = state;
            var draft = LiveSeed.NewAgent(user, "draft");
            var stopped = LiveSeed.NewAgent(user, "stopped");
            stopped.State = AgentState.Stopped;
            db.Attach(user);
            db.AddRange(active, draft, stopped, LiveSeed.NewLogEntry(LiveSeed.Now, user.Id));
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(AccountDeletionStatus.InvalidPassword, (await accounts.DeleteAccountAsync(user.Id, "wrong password", Address, Ct)).Status);
        var refused = await accounts.DeleteAccountAsync(user.Id, Password, Address, Ct);

        Assert.Equal(AccountDeletionStatus.AgentsNotStopped, refused.Status);
        Assert.Equal(1, refused.BlockingAgents);
        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(1, await db.Users.CountAsync(u => u.Id == user.Id, Ct));
            Assert.Equal(3, await db.Agents.CountAsync(a => a.UserId == user.Id, Ct));
            Assert.Equal(1, await db.LogEntries.CountAsync(l => l.UserId == user.Id, Ct));
        }

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task DeletionRemovesEverythingTheUserOwnsAndNothingElse()
    {
        await StartAsync();
        var (provider, logs, _) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        var hasher = provider.GetRequiredService<IPasswordHasher<User>>();
        Guid deletedId, otherId;
        await using (var db = _fixture.CreateContext())
        {
            var (user, _, _, _) = await LiveSeed.SeedEverythingAsync(db, Ct);
            user.PasswordHash = hasher.HashPassword(user, Password);
            var other = LiveSeed.NewUser("other@example.com");
            var otherAgent = LiveSeed.NewAgent(other, "other");
            otherAgent.State = AgentState.Stopped;
            db.AddRange(other, otherAgent);
            // The id is generated on Add, so the log entry is built afterwards.
            db.AddRange(LiveSeed.NewLogEntry(LiveSeed.Now, other.Id), LiveSeed.NewLogEntry(LiveSeed.Now));
            await db.SaveChangesAsync(Ct);
            deletedId = user.Id;
            otherId = other.Id;
        }

        var result = await accounts.DeleteAccountAsync(deletedId, Password, Address, Ct);

        Assert.Equal(AccountDeletionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.DeletedLogEntries);
        await using (var db = _fixture.CreateContext())
        {
            Assert.Equal(0, await db.Users.CountAsync(u => u.Id == deletedId, Ct));
            Assert.Equal(0, await db.ExchangeKeys.CountAsync(k => k.UserId == deletedId, Ct));
            Assert.Equal(0, await db.ProviderKeys.CountAsync(k => k.UserId == deletedId, Ct));
            Assert.Equal(0, await db.Agents.CountAsync(a => a.UserId == deletedId, Ct));
            Assert.Equal(0, await db.Evaluations.CountAsync(e => e.Agent.UserId == deletedId, Ct));
            Assert.Equal(0, await db.Decisions.CountAsync(d => d.Evaluation.Agent.UserId == deletedId, Ct));
            Assert.Equal(0, await db.Trades.CountAsync(t => t.Agent.UserId == deletedId, Ct));
            Assert.Equal(0, await db.Orders.CountAsync(o => o.Agent.UserId == deletedId, Ct));
            Assert.Equal(0, await db.DisclaimerAcceptances.CountAsync(a => a.UserId == deletedId, Ct));
            Assert.Equal(0, await db.ExperienceStats.CountAsync(s => s.UserId == deletedId, Ct));
            Assert.Equal(0, await db.LogEntries.CountAsync(l => l.UserId == deletedId, Ct));

            Assert.Equal(1, await db.Users.CountAsync(u => u.Id == otherId, Ct));
            Assert.Equal(1, await db.Agents.CountAsync(a => a.UserId == otherId, Ct));
            Assert.Equal(1, await db.LogEntries.CountAsync(l => l.UserId == otherId, Ct));
            Assert.Equal(1, await db.LogEntries.CountAsync(l => l.UserId == null, Ct));
            Assert.Equal(1, await db.Candles.CountAsync(Ct));
            Assert.Equal(1, await db.ProviderModels.CountAsync(Ct));
            Assert.Equal(1, await db.ProviderSyncStates.CountAsync(Ct));
            Assert.Equal(1, await db.SystemKeys.CountAsync(Ct));
            Assert.Equal(1, await db.ExperienceStats.CountAsync(s => s.Scope == StatScope.System, Ct));
        }

        Assert.Equal(AuthenticationStatus.InvalidCredentials, (await accounts.AuthenticateAsync("trader@example.com", Password, Address, Ct)).Status);
        Assert.Null(await provider.GetRequiredService<ISecurityStampSource>().FindAsync(deletedId, Ct));
        Assert.True(provider.GetRequiredService<SessionRevocations>().IsRevokedSince(deletedId, LiveSeed.Now));
        Assert.Equal(AccountDeletionStatus.UserNotFound, (await accounts.DeleteAccountAsync(deletedId, Password, Address, Ct)).Status);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"User {deletedId} deleted their account; 1 log entries removed");
        AssertNoCredentials(logs, "trader@example.com");

        await DeleteUsersAsync(otherId);
        await using (var cleanup = _fixture.CreateContext())
        {
            await cleanup.LogEntries.ExecuteDeleteAsync(Ct);
            await cleanup.Candles.ExecuteDeleteAsync(Ct);
            await cleanup.ProviderModels.ExecuteDeleteAsync(Ct);
            await cleanup.ProviderSyncStates.ExecuteDeleteAsync(Ct);
            await cleanup.SystemKeys.ExecuteDeleteAsync(Ct);
            await cleanup.ExperienceStats.ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task StampSourceReadsTheCurrentStampAndNullForAMissingUser()
    {
        await StartAsync();
        var (provider, _, _) = Build();
        await using var _ = provider;
        var user = LiveSeed.NewUser("stamp@example.com");
        await using (var db = _fixture.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync(Ct);
        }
        var stamps = provider.GetRequiredService<ISecurityStampSource>();

        Assert.Equal(user.SecurityStamp, await stamps.FindAsync(user.Id, Ct));
        Assert.Null(await stamps.FindAsync(Guid.NewGuid(), Ct));

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task PreferencesRoundTripAndAnUnknownUserIsNullAndFalse()
    {
        await StartAsync();
        var (provider, logs, _) = Build();
        await using var _ = provider;
        var accounts = provider.GetRequiredService<IAccountService>();
        var user = LiveSeed.NewUser("prefs@example.com");
        await using (var db = _fixture.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(new UserPreferences(UiTheme.System, false), await accounts.GetPreferencesAsync(user.Id, Ct));
        Assert.True(await accounts.UpdatePreferencesAsync(user.Id, new UserPreferences(UiTheme.Dark, true), Ct));
        Assert.Equal(new UserPreferences(UiTheme.Dark, true), await accounts.GetPreferencesAsync(user.Id, Ct));
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
            Assert.Equal(UiTheme.Dark, row.Theme);
            Assert.True(row.ReducedMotion);
        }

        Assert.Null(await accounts.GetPreferencesAsync(Guid.NewGuid(), Ct));
        Assert.False(await accounts.UpdatePreferencesAsync(Guid.NewGuid(), new UserPreferences(UiTheme.Light, false), Ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"User {user.Id} chose the Dark theme with reduced motion True");
        AssertNoCredentials(logs, "prefs@example.com");

        await DeleteUsersAsync(user.Id);
    }

    [Fact]
    public async Task RegistrationAppliesTheUiDefaults()
    {
        await StartAsync();
        await using var provider = AccountsTestHost.BuildServices(_fixture.ConnectionString, new TestLoggerProvider(), new() { ["Ui:DefaultTheme"] = "Dark", ["Ui:ReducedMotion"] = "true" }, new FakeTimeProvider(LiveSeed.Now));
        var accounts = provider.GetRequiredService<IAccountService>();

        var registered = await accounts.RegisterAsync("defaults@example.com", Password, Address, Ct);

        Assert.Equal(RegistrationStatus.Succeeded, registered.Status);
        Assert.Equal(UiTheme.Dark, registered.User!.Theme);
        Assert.True(registered.User.ReducedMotion);
        Assert.Equal(new UserPreferences(UiTheme.Dark, true), await accounts.GetPreferencesAsync(registered.User.Id, Ct));

        await DeleteUsersAsync(registered.User.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }

    private (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time) Build()
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(LiveSeed.Now);
        return (AccountsTestHost.BuildServices(_fixture.ConnectionString, logs, time: time), logs, time);
    }

    private async Task DeleteUsersAsync(params Guid[] ids)
    {
        await using var db = _fixture.CreateContext();
        await db.LogEntries.Where(l => l.UserId != null && ids.Contains(l.UserId.Value)).ExecuteDeleteAsync(Ct);
        await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync(Ct);
    }

    private static void AssertNoCredentials(TestLoggerProvider logs, string email)
    {
        Assert.DoesNotContain(Password, logs.AllText);
        Assert.DoesNotContain(email, logs.AllText, StringComparison.OrdinalIgnoreCase);
    }
}
