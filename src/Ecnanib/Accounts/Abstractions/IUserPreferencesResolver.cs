using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// The theme and animation preference a page is rendered with (FR-UI-02): the signed-in user's own, read once per
/// request by primary key so the document element carries it before the first paint, or the <c>Ui</c> section's
/// defaults for a visitor. It never throws and never queries while an error or status-code page is rendered, so a
/// database outage still produces the error page.
/// </summary>
public interface IUserPreferencesResolver
{
    Task<UserPreferences> ResolveAsync(HttpContext? httpContext, CancellationToken cancellationToken);
}
