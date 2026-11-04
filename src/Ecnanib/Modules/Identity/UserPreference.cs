using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// A user's preferences (SRS 6.1). One per user, and only once they've saved some: without a row, the
/// configured defaults apply.
/// </summary>
internal sealed class UserPreference
{
    /// <summary>The longest name of a <see cref="Contracts.Theme"/> or <see cref="AnimationLevel"/> value, with room to spare.</summary>
    public const int ValueMaxLength = 16;

    public required Guid UserId { get; init; }

    public required Theme Theme { get; init; }

    public required AnimationLevel Animation { get; init; }

    /// <summary>An IANA ID, or null for the browser's.</summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// The <see cref="NotificationType"/> names the user wants by email (FR-PREF-06), or null if they
    /// never chose, when <see cref="EmailNotificationChoices.Defaults"/> apply. Empty means none.
    /// </summary>
    public string[]? EmailNotifications { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>In declaration order, so the same choice is always stored the same way.</summary>
    public static string[] Names(IEnumerable<NotificationType> types) =>
        [.. types.Distinct().Order().Select(type => type.ToString())];

    /// <summary>
    /// What a user is emailed: their choice, or the defaults, and the administrator-only types only for an
    /// administrator. A name this build doesn't know is skipped, because values are never renamed.
    /// </summary>
    public static IReadOnlySet<NotificationType> EffectiveEmailNotifications(string[]? stored, bool administrator)
    {
        var chosen = stored is null
            ? EmailNotificationChoices.Defaults
            : stored.Where(name => Enum.GetNames<NotificationType>().Contains(name, StringComparer.Ordinal)).Select(Enum.Parse<NotificationType>);

        return chosen.Where(type => administrator || !EmailNotificationChoices.AdministratorOnly.Contains(type)).ToHashSet();
    }
}

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("user_preferences");
        builder.HasKey(preference => preference.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(preference => preference.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(preference => preference.Theme).HasConversion<string>().HasMaxLength(UserPreference.ValueMaxLength);
        builder.Property(preference => preference.Animation).HasConversion<string>().HasMaxLength(UserPreference.ValueMaxLength);
        builder.Property(preference => preference.TimeZone).HasMaxLength(TimeZones.MaxIdLength);
        builder.Property(preference => preference.EmailNotifications).HasColumnType("text[]");
    }
}
