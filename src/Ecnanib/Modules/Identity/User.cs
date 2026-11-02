using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>An account (SRS 6.1). Sign-in counters live here because a lock has to survive a restart.</summary>
internal sealed class User
{
    public Guid Id { get; init; }

    /// <summary>As the user typed it, for showing back to them.</summary>
    public required string Email { get; init; }

    /// <summary>What identifies the account. See <see cref="EmailAddresses.Normalize"/>.</summary>
    public required string NormalizedEmail { get; init; }

    public required string PasswordHash { get; init; }

    /// <summary>Null until a link (#13) or the bootstrap secret proves the address.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; init; }

    /// <summary>Failed attempts since the last sign-in or lock.</summary>
    public int FailedSignIns { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Email).HasMaxLength(EmailAddresses.MaximumLength);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(EmailAddresses.MaximumLength);
        builder.HasIndex(user => user.NormalizedEmail).IsUnique();
    }
}
