using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>Not audited, because FR-AUD-01 doesn't list preferences.</summary>
internal sealed class Preferences(IDbContextFactory<EcnanibDbContext> contextFactory, IdentitySettings settings) : IPreferences
{
    public UserPreferences ForVisitors { get; } = new(Theme.System, settings.DefaultAnimation, null);

    private UserPreferences Defaults { get; } = new(settings.DefaultTheme, settings.DefaultAnimation, null);

    public async Task<UserPreferences> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<UserPreference>()
            .Where(preference => preference.UserId == userId)
            .Select(preference => new UserPreferences(preference.Theme, preference.Animation, preference.TimeZone))
            .SingleOrDefaultAsync(cancellationToken)
            ?? Defaults;
    }

    public async Task<PreferencesResult> SaveAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken)
    {
        if (preferences.TimeZone is { } timeZone && !TimeZones.IsOffered(timeZone))
        {
            return PreferencesResult.UnknownTimeZone;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Selected from users, so an account deleted meanwhile inserts nothing rather than failing the key.
        await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO user_preferences (user_id, theme, animation, time_zone, updated_at)
            SELECT id, {preferences.Theme.ToString()}, {preferences.Animation.ToString()}, {preferences.TimeZone}, {DateTimeOffset.UtcNow}
            FROM users
            WHERE id = {userId}
            ON CONFLICT (user_id) DO UPDATE
            SET theme = excluded.theme, animation = excluded.animation, time_zone = excluded.time_zone, updated_at = excluded.updated_at
            """,
            cancellationToken);

        return PreferencesResult.Saved;
    }
}
