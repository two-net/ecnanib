using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Re-checks the session cookie against the account through <see cref="SessionValidator"/>. A kept ticket is renewed
/// (its issue time advances and the expiry slides); a rejected one is dropped and the cookie deleted.
/// </summary>
internal sealed class AccountCookieEvents : CookieAuthenticationEvents
{
    private readonly SessionValidator _validator;

    public AccountCookieEvents(SessionValidator validator)
    {
        _validator = validator;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var check = await _validator.CheckAsync(context.Principal, context.Properties.IssuedUtc, context.HttpContext.RequestAborted).ConfigureAwait(false);
        switch (check)
        {
            case SessionCheck.Kept:
                context.ShouldRenew = true;
                break;
            case SessionCheck.Rejected:
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name).ConfigureAwait(false);
                break;
        }
    }
}
