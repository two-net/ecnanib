using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// A sign-in that waits for a code, as a row of the <c>pending_sign_ins</c> table: the browser gave
/// the password of an account with two-factor authentication, and has not given a code yet
/// (FR-ACC-3). It is not a session, and it opens no page. See docs/accounts.md.
/// </summary>
/// <param name="id">Assigned by the application when the password is accepted.</param>
/// <param name="userId">The account whose password the browser gave.</param>
/// <param name="tokenHash">The SHA-256 hash of the token in the browser's cookie.</param>
/// <param name="expiresAt">When the code can no longer be given, and the password is asked for again.</param>
public sealed class PendingSignIn(Guid id, Guid userId, byte[] tokenHash, DateTimeOffset expiresAt)
{
    /// <summary>How long a browser has to give the code after it gave the password.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public byte[] TokenHash { get; } = tokenHash;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

internal sealed class PendingSignInConfiguration : IEntityTypeConfiguration<PendingSignIn>
{
    public void Configure(EntityTypeBuilder<PendingSignIn> builder)
    {
        builder.ToTable("pending_sign_ins");
        builder.HasKey(pending => pending.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(pending => pending.Id).ValueGeneratedNever();
        builder.Property(pending => pending.UserId);
        builder.Property(pending => pending.TokenHash);
        builder.Property(pending => pending.ExpiresAt);

        // It is looked up by the hash of its token.
        builder.HasIndex(pending => pending.TokenHash).IsUnique();

        // It belongs to the account and goes with it when it is deleted. The key also gives the
        // index that ending every one of an account uses.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(pending => pending.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
