using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// The one live link of each kind that an account has, for verifying its address (FR-AUTH-03) or
/// resetting its password (FR-AUTH-06). Using a link deletes its row, so each works once, and issuing
/// a new one replaces the old. Only the token's hash is stored, so the table can't be read for links.
/// </summary>
internal sealed class AccountToken
{
    public required Guid UserId { get; init; }

    public required AccountTokenPurpose Purpose { get; init; }

    /// <summary>SHA-256 of the token's bytes. The token itself is only ever in the link.</summary>
    public required byte[] TokenHash { get; init; }

    public required AccountTokenDelivery Delivery { get; init; }

    /// <summary>When it was issued, which the throttle on sending another reads.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

internal enum AccountTokenPurpose
{
    EmailVerification,
    PasswordReset,
}

/// <summary>How the link reached its user, which decides what using it proves.</summary>
internal enum AccountTokenDelivery
{
    /// <summary>Sent to the account's address, so using it proves the user reads that address.</summary>
    Email,

    /// <summary>Handed out by an administrator, so it proves nothing about the address.</summary>
    Administrator,
}

internal sealed class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> builder)
    {
        builder.ToTable("account_tokens");
        builder.HasKey(token => new { token.UserId, token.Purpose });
        builder.Property(token => token.Purpose).HasConversion<string>();
        builder.Property(token => token.Delivery).HasConversion<string>();
        builder.Property(token => token.TokenHash).HasMaxLength(AccountTokens.HashLength);
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
