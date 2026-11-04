using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>Not audited, because FR-AUD-01 doesn't list preferences.</summary>
internal sealed class Preferences(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IAdministrators administrators,
    IdentitySettings settings) : IPreferences
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

    public Task<PreferencesResult> SaveAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken) =>
        SaveAsync(userId, preferences, emailNotifications: null, cancellationToken);

    public async Task<PreferencesResult> SaveAsync(
        Guid userId,
        UserPreferences preferences,
        IReadOnlySet<NotificationType>? emailNotifications,
        CancellationToken cancellationToken)
    {
        if (preferences.TimeZone is { } timeZone && !TimeZones.IsOffered(timeZone))
        {
            return PreferencesResult.UnknownTimeZone;
        }

        // With email off nothing offers the choices, so whatever arrives leaves the stored ones alone.
        var emailChoices = settings.EmailEnabled ? emailNotifications : null;

        if (emailChoices is not null
            && emailChoices.Overlaps(EmailNotificationChoices.AdministratorOnly)
            && !await administrators.IsAdministratorAsync(userId, cancellationToken))
        {
            return PreferencesResult.UnavailableEmailNotification;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // Selected from users, so an account deleted meanwhile inserts nothing rather than failing the key.
        if (emailChoices is null)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO user_preferences (user_id, theme, animation, time_zone, updated_at)
                SELECT id, {preferences.Theme.ToString()}, {preferences.Animation.ToString()}, {preferences.TimeZone}, {now}
                FROM users
                WHERE id = {userId}
                ON CONFLICT (user_id) DO UPDATE
                SET theme = excluded.theme, animation = excluded.animation, time_zone = excluded.time_zone, updated_at = excluded.updated_at
                """,
                cancellationToken);
        }
        else
        {
            var names = UserPreference.Names(emailChoices);

            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO user_preferences (user_id, theme, animation, time_zone, email_notifications, updated_at)
                SELECT id, {preferences.Theme.ToString()}, {preferences.Animation.ToString()}, {preferences.TimeZone}, {names}::text[], {now}
                FROM users
                WHERE id = {userId}
                ON CONFLICT (user_id) DO UPDATE
                SET theme = excluded.theme, animation = excluded.animation, time_zone = excluded.time_zone,
                    email_notifications = excluded.email_notifications, updated_at = excluded.updated_at
                """,
                cancellationToken);
        }

        return PreferencesResult.Saved;
    }

    public async Task<IReadOnlySet<NotificationType>> GetEmailNotificationsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await context.Set<UserPreference>()
            .Where(preference => preference.UserId == userId)
            .Select(preference => preference.EmailNotifications)
            .SingleOrDefaultAsync(cancellationToken);

        return UserPreference.EffectiveEmailNotifications(stored, await administrators.IsAdministratorAsync(userId, cancellationToken));
    }
}
