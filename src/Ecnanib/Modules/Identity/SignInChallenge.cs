using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// A sign-in waiting for its two-factor code: the password was right, and no session exists yet. One
/// per account, so a second sign-in replaces the first's. Only the token's hash is stored, and the
/// browser holds the token in a cookie. See Two-factor in docs/architecture.md.
/// </summary>
internal sealed class SignInChallenge
{
    public required Guid UserId { get; init; }

    /// <summary>SHA-256 of the token's bytes.</summary>
    public required byte[] TokenHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class SignInChallengeConfiguration : IEntityTypeConfiguration<SignInChallenge>
{
    public void Configure(EntityTypeBuilder<SignInChallenge> builder)
    {
        builder.ToTable("sign_in_challenges");
        builder.HasKey(challenge => challenge.UserId);
        builder.Property(challenge => challenge.TokenHash).HasMaxLength(AccountTokens.HashLength);
        builder.HasIndex(challenge => challenge.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(challenge => challenge.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
