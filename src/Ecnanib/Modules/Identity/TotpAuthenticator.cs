using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// A user's authenticator (FR-AUTH-05): the encrypted TOTP secret SRS 6.1 gives the user, and what
/// guards its codes. One per user. It's pending until a code from it confirms it, and only then does
/// sign-in ask for one. See Two-factor in docs/architecture.md.
/// </summary>
internal sealed class TotpAuthenticator
{
    public required Guid UserId { get; init; }

    /// <summary>The row in <c>secrets</c> holding the key as base32. Nothing else refers to it, so this owner deletes it.</summary>
    public required Guid SecretId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null while setup is pending, so two-factor isn't on yet.</summary>
    public DateTimeOffset? EnabledAt { get; init; }

    /// <summary>The time step of the last code accepted. A code is only accepted for a later step, so none works twice.</summary>
    public long LastUsedStep { get; init; }

    /// <summary>
    /// Wrong codes for sensitive actions since the last right one or lock. Separate from the sign-in
    /// lockout, so that a stranger's wrong passwords can't keep a signed-in owner from the kill switch.
    /// </summary>
    public int FailedChecks { get; init; }

    public DateTimeOffset? ChecksLockedUntil { get; init; }
}

internal sealed class TotpAuthenticatorConfiguration : IEntityTypeConfiguration<TotpAuthenticator>
{
    public void Configure(EntityTypeBuilder<TotpAuthenticator> builder)
    {
        builder.ToTable("totp_authenticators");
        builder.HasKey(authenticator => authenticator.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(authenticator => authenticator.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
