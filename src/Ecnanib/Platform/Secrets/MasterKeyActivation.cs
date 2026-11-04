using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// A start at which the active master key differed from the previous start's. The newest row is
/// the key in use, so a rotation is recorded and audited once, however often the application restarts.
/// </summary>
internal sealed class MasterKeyActivation
{
    public long Id { get; init; }

    public required string KeyId { get; init; }

    public required DateTimeOffset ActivatedAt { get; init; }
}

internal sealed class MasterKeyActivationConfiguration : IEntityTypeConfiguration<MasterKeyActivation>
{
    public void Configure(EntityTypeBuilder<MasterKeyActivation> builder)
    {
        builder.ToTable("master_key_activations");
        builder.HasKey(activation => activation.Id);
        builder.Property(activation => activation.KeyId).HasMaxLength(MasterKeys.KeyIdMaxLength);
    }
}
