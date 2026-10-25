using Ecnanib.Accounts.Abstractions;
using Ecnanib.Ui.Abstractions;
using Ecnanib.Ui.Configuration;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Services;

/// <summary>
/// The theme and animation preference a page is rendered with (FR-UI-02): the signed-in user's own, read once per
/// request by primary key so the document element carries it before the first paint, or the <c>Ui</c> section's
/// defaults for a visitor. It never throws and never queries while an error or status-code page is rendered, so a
/// database outage still produces the error page.
/// </summary>
public sealed class UserPreferencesResolver
{
    private readonly IAccountService _accounts;
    private readonly IOptions<UiOptions> _ui;
    private readonly ILogger<UserPreferencesResolver> _logger;

    public UserPreferencesResolver(IAccountService accounts, IOptions<UiOptions> ui, ILogger<UserPreferencesResolver> logger)
    {
        _accounts = accounts;
        _ui = ui;
        _logger = logger;
    }

    public async Task<UserPreferences> ResolveAsync(HttpContext? httpContext, CancellationToken cancellationToken)
    {
        var defaults = _ui.Value.Defaults;
        if (httpContext is null
            || httpContext.Features.Get<IExceptionHandlerFeature>() is not null
            || httpContext.Features.Get<IStatusCodeReExecuteFeature>() is not null
            || httpContext.User.GetUserId() is not { } userId)
        {
            return defaults;
        }

        try
        {
            return await _accounts.GetPreferencesAsync(userId, cancellationToken).ConfigureAwait(false) ?? defaults;
        }
        catch (Exception ex)
        {
            // The page still renders, in the defaults; the failure surfaces where the data is actually needed.
            _logger.LogDebug(ex, "The preferences of user {UserId} could not be read; the defaults apply", userId);
            return defaults;
        }
    }
}
