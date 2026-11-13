using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Credentials;

/// <summary>
/// A user's key for one AI provider, as a row of the <c>ai_credentials</c> table (FR-KEY-2). The
/// key is stored encrypted, and nothing of it is stored readable but its last four characters
/// (FR-KEY-6, FR-KEY-8). See docs/credentials.md.
/// </summary>
/// <param name="id">
/// Assigned by the application when the first key for the provider is stored. It stays the same
/// when the key is replaced, so that what points at the key still does.
/// </param>
/// <param name="userId">The user whose key it is.</param>
/// <param name="provider">The provider the key is for. A user has one key for each.</param>
/// <param name="encryptedValue">The key, as <c>StoredSecrets</c> encrypted it.</param>
/// <param name="masterKeyId">The id of the master key of the configuration file that encrypted it.</param>
/// <param name="maskedSuffix">The last four characters of the key, which is all of it that is ever shown.</param>
/// <param name="savedAt">When the key that is stored now was stored.</param>
public sealed class AiCredential(
    Guid id,
    Guid userId,
    AiProviderCode provider,
    byte[] encryptedValue,
    string masterKeyId,
    string maskedSuffix,
    DateTimeOffset savedAt) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public AiProviderCode Provider { get; } = provider;

    public byte[] EncryptedValue { get; } = encryptedValue;

    public string MasterKeyId { get; } = masterKeyId;

    public string MaskedSuffix { get; } = maskedSuffix;

    public DateTimeOffset SavedAt { get; } = savedAt;

    /// <summary>
    /// What the stored key of a row is bound to, so that it decrypts in that row of that user
    /// only: a value that is copied to another row, or a row that is given to another user, is of
    /// no use there.
    /// </summary>
    public static string BoundTo(Guid id, Guid userId) => $"ai-key:{userId}:{id}";
}

internal sealed class AiCredentialConfiguration : IEntityTypeConfiguration<AiCredential>
{
    public void Configure(EntityTypeBuilder<AiCredential> builder)
    {
        builder.ToTable("ai_credentials");
        builder.HasKey(credential => credential.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(credential => credential.Id).ValueGeneratedNever();
        builder.Property(credential => credential.UserId);
        builder.Property(credential => credential.Provider);
        builder.Property(credential => credential.EncryptedValue);
        builder.Property(credential => credential.MasterKeyId);
        builder.Property(credential => credential.MaskedSuffix);
        builder.Property(credential => credential.SavedAt);

        // One key for each provider. The index also finds the keys of a user.
        builder.HasIndex(credential => new { credential.UserId, credential.Provider }).IsUnique();

        // The keys belong to the account and go with it when it is deleted, at once (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A provider is a seeded row, which is never deleted while a key points at it.
        builder.HasOne<AiProvider>()
            .WithMany()
            .HasForeignKey(credential => credential.Provider)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
