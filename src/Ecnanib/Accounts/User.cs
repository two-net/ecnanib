using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// An account, as a row of the <c>users</c> table. The only account type is the individual user
/// (FR-ACC-5). The personal data in it is the email address (NFR-SEC-9).
/// </summary>
/// <param name="id">Assigned by the application when the account is created.</param>
/// <param name="email">The address, trimmed and in lower case, so that one address is one account.</param>
/// <param name="passwordHash">The password as a salted Argon2id hash, and never the password (NFR-SEC-2).</param>
/// <param name="registeredAt">When the account was created.</param>
/// <param name="emailVerifiedAt">When the address was verified, or null while it is not (FR-ACC-1).</param>
/// <param name="emailVerificationTokenHash">
/// The SHA-256 hash of the token in the verification link that is out, or null when none is. The
/// token itself is only in the email.
/// </param>
/// <param name="emailVerificationExpiresAt">When that link stops working.</param>
public sealed class User(
    Guid id,
    string email,
    string passwordHash,
    DateTimeOffset registeredAt,
    DateTimeOffset? emailVerifiedAt,
    byte[]? emailVerificationTokenHash,
    DateTimeOffset? emailVerificationExpiresAt)
{
    public Guid Id { get; } = id;

    public string Email { get; } = email;

    public string PasswordHash { get; } = passwordHash;

    public DateTimeOffset RegisteredAt { get; } = registeredAt;

    public DateTimeOffset? EmailVerifiedAt { get; } = emailVerifiedAt;

    public byte[]? EmailVerificationTokenHash { get; } = emailVerificationTokenHash;

    public DateTimeOffset? EmailVerificationExpiresAt { get; } = emailVerificationExpiresAt;
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.Email);
        builder.Property(user => user.PasswordHash);
        builder.Property(user => user.RegisteredAt);
        builder.Property(user => user.EmailVerifiedAt);
        builder.Property(user => user.EmailVerificationTokenHash);
        builder.Property(user => user.EmailVerificationExpiresAt);

        // One account for each address. Registration relies on it: see Registration.
        builder.HasIndex(user => user.Email).IsUnique();

        // A link is looked up by the hash of its token.
        builder.HasIndex(user => user.EmailVerificationTokenHash).IsUnique();
    }
}
