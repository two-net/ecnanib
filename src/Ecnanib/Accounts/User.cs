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
/// <param name="emailVerificationSetsPassword">
/// True when that link came from registering the address again. Whoever confirms it chooses the
/// password of the account, because the one stored may be someone else's.
/// </param>
/// <param name="passwordResetTokenHash">
/// The SHA-256 hash of the token in the password reset link that is out, or null when none is
/// (FR-ACC-2).
/// </param>
/// <param name="passwordResetExpiresAt">When that link stops working.</param>
/// <param name="twoFactorSecret">
/// The key that the account shares with an authenticator app, encrypted, or null while two-factor
/// authentication is off (FR-ACC-3, NFR-SEC-5). It is never stored in any other form.
/// </param>
/// <param name="twoFactorSecretKeyId">The id of the master key that encrypted that key (FR-KEY-6).</param>
/// <param name="twoFactorEnabledAt">
/// When two-factor authentication was turned on. It is on exactly while this is set, and the key
/// and its key id are set with it and cleared with it.
/// </param>
/// <param name="twoFactorLastStep">
/// The step of time of the last code from the app that was accepted. A code is accepted only for
/// a later step, so no code is accepted twice.
/// </param>
/// <param name="twoFactorFailedCodes">How many wrong codes were entered in a row.</param>
/// <param name="twoFactorLockedUntil">Until when every code is refused, after too many wrong ones.</param>
public sealed class User(
    Guid id,
    string email,
    string passwordHash,
    DateTimeOffset registeredAt,
    DateTimeOffset? emailVerifiedAt,
    byte[]? emailVerificationTokenHash,
    DateTimeOffset? emailVerificationExpiresAt,
    bool emailVerificationSetsPassword,
    byte[]? passwordResetTokenHash,
    DateTimeOffset? passwordResetExpiresAt,
    byte[]? twoFactorSecret,
    string? twoFactorSecretKeyId,
    DateTimeOffset? twoFactorEnabledAt,
    long? twoFactorLastStep,
    int twoFactorFailedCodes,
    DateTimeOffset? twoFactorLockedUntil)
{
    public Guid Id { get; } = id;

    public string Email { get; } = email;

    public string PasswordHash { get; } = passwordHash;

    public DateTimeOffset RegisteredAt { get; } = registeredAt;

    public DateTimeOffset? EmailVerifiedAt { get; } = emailVerifiedAt;

    public byte[]? EmailVerificationTokenHash { get; } = emailVerificationTokenHash;

    public DateTimeOffset? EmailVerificationExpiresAt { get; } = emailVerificationExpiresAt;

    public bool EmailVerificationSetsPassword { get; } = emailVerificationSetsPassword;

    public byte[]? PasswordResetTokenHash { get; } = passwordResetTokenHash;

    public DateTimeOffset? PasswordResetExpiresAt { get; } = passwordResetExpiresAt;

    public byte[]? TwoFactorSecret { get; } = twoFactorSecret;

    public string? TwoFactorSecretKeyId { get; } = twoFactorSecretKeyId;

    public DateTimeOffset? TwoFactorEnabledAt { get; } = twoFactorEnabledAt;

    public long? TwoFactorLastStep { get; } = twoFactorLastStep;

    public int TwoFactorFailedCodes { get; } = twoFactorFailedCodes;

    public DateTimeOffset? TwoFactorLockedUntil { get; } = twoFactorLockedUntil;
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
        builder.Property(user => user.EmailVerificationSetsPassword);
        builder.Property(user => user.PasswordResetTokenHash);
        builder.Property(user => user.PasswordResetExpiresAt);

        // Two-factor authentication. None of these has an index: the account is found by its id.
        builder.Property(user => user.TwoFactorSecret);
        builder.Property(user => user.TwoFactorSecretKeyId);
        builder.Property(user => user.TwoFactorEnabledAt);
        builder.Property(user => user.TwoFactorLastStep);
        builder.Property(user => user.TwoFactorFailedCodes);
        builder.Property(user => user.TwoFactorLockedUntil);

        // One account for each address. Registration relies on it: see Registration.
        builder.HasIndex(user => user.Email).IsUnique();

        // A link is looked up by the hash of its token.
        builder.HasIndex(user => user.EmailVerificationTokenHash).IsUnique();
        builder.HasIndex(user => user.PasswordResetTokenHash).IsUnique();
    }
}
