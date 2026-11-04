using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// One encrypted secret (FR-KEY-07). The row its owner keeps, such as a credential, refers to it by
/// ID, with no foreign key either way, so this table knows nothing about owners. Data Protection
/// keys are stored here too. See Secrets in docs/architecture.md.
/// </summary>
internal sealed class Secret
{
    public Guid Id { get; init; }

    /// <summary>
    /// The master key that sealed it. A concurrency token, so re-encrypting a row that an owner
    /// replaced or deleted meanwhile fails, rather than writing over it.
    /// </summary>
    public required string KeyId { get; set; }

    /// <summary>What <see cref="SecretCipher.Seal"/> returned.</summary>
    public required byte[] Ciphertext { get; set; }
}

internal sealed class SecretConfiguration : IEntityTypeConfiguration<Secret>
{
    public void Configure(EntityTypeBuilder<Secret> builder)
    {
        builder.ToTable("secrets");
        builder.HasKey(secret => secret.Id);
        builder.Property(secret => secret.Id).ValueGeneratedNever();
        builder.Property(secret => secret.KeyId).HasMaxLength(MasterKeys.KeyIdMaxLength).IsConcurrencyToken();

        // Startup and the health page count secrets per key.
        builder.HasIndex(secret => secret.KeyId);
    }
}
