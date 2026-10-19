using Ecnanib.Accounts.Configuration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Authentication state for interactive server circuits, re-validated every <c>Accounts:SessionRevalidationMinutes</c>
/// with the same rule as the cookie (FR-ACC-03). Static server rendering takes its state from the request instead.
/// </summary>
internal sealed class AccountAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly SessionValidator _validator;
    private readonly IOptions<AccountsOptions> _options;

    public AccountAuthenticationStateProvider(ILoggerFactory loggerFactory, SessionValidator validator, IOptions<AccountsOptions> options)
        : base(loggerFactory)
    {
        _validator = validator;
        _options = options;
    }

    protected override TimeSpan RevalidationInterval => _options.Value.SessionRevalidation;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        await _validator.CheckAsync(authenticationState.User, issuedUtc: null, cancellationToken).ConfigureAwait(false) != SessionCheck.Rejected;
}
