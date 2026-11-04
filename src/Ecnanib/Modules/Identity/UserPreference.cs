using Ecnanib.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// A user's preferences (SRS 6.1). One per user, and only once they've saved some: without a row, the
/// configured defaults apply. Notification settings (FR-PREF-06) belong here too.
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

    public required DateTimeOffset UpdatedAt { get; init; }
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
    }
}
