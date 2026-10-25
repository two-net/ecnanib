using System.Security.Claims;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Accounts.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Ui.Abstractions;
using Ecnanib.Ui.Configuration;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Services;

public class UserPreferencesResolverTests
{
    private static readonly UserPreferences Defaults = new(UiTheme.Dark, true);
    private static readonly UserPreferences Stored = new(UiTheme.Light, false);
    private static readonly Guid UserId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVisitorGetsTheUiDefaultsWithoutAQuery()
    {
        var accounts = new RecordingAccounts();

        Assert.Equal(Defaults, await Build(accounts).ResolveAsync(new DefaultHttpContext(), Ct));
        Assert.Equal(Defaults, await Build(accounts).ResolveAsync(null, Ct));
        Assert.Equal(0, accounts.Calls);
    }

    [Fact]
    public async Task ASignedInUserGetsTheirOwnPreferenceInOneCall()
    {
        var accounts = new RecordingAccounts { Stored = Stored };

        var preferences = await Build(accounts).ResolveAsync(SignedIn(), Ct);

        Assert.Equal(Stored, preferences);
        Assert.Equal(1, accounts.Calls);
        Assert.Equal(UserId, accounts.LastUserId);
    }

    [Fact]
    public async Task AnErrorOrStatusCodePageNeverQueries()
    {
        var accounts = new RecordingAccounts { Stored = Stored };
        var failed = SignedIn();
        failed.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = new InvalidOperationException("boom") });
        var reExecuted = SignedIn();
        reExecuted.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature { OriginalPath = "/missing" });

        Assert.Equal(Defaults, await Build(accounts).ResolveAsync(failed, Ct));
        Assert.Equal(Defaults, await Build(accounts).ResolveAsync(reExecuted, Ct));
        Assert.Equal(0, accounts.Calls);
    }

    [Fact]
    public async Task AGoneUserOrAFailingDatabaseFallsBackToTheDefaults()
    {
        Assert.Equal(Defaults, await Build(new RecordingAccounts { Stored = null }).ResolveAsync(SignedIn(), Ct));
        Assert.Equal(Defaults, await Build(new RecordingAccounts { Throw = true }).ResolveAsync(SignedIn(), Ct));
    }

    private static UserPreferencesResolver Build(IAccountService accounts) =>
        new(accounts, Options.Create(new UiOptions { DefaultTheme = "Dark", ReducedMotion = true }), NullLogger<UserPreferencesResolver>.Instance);

    private static DefaultHttpContext SignedIn()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D"))], "test"));
        return context;
    }

    private sealed class RecordingAccounts : IAccountService
    {
        public UserPreferences? Stored { get; init; }

        public bool Throw { get; init; }

        public int Calls { get; private set; }

        public Guid? LastUserId { get; private set; }

        public Task<UserPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls++;
            LastUserId = userId;
            return Throw ? throw new TimeoutException("database down") : Task.FromResult(Stored);
        }

        public Task<bool> UpdatePreferencesAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RegistrationResult> RegisterAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthenticationResult> AuthenticateAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, string? clientAddress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, string? password, string? clientAddress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DisclaimerStatus> GetDisclaimerStatusAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DisclaimerStatus> AcceptDisclaimerAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
