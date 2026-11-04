using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// One unused recovery code, for a user who has lost their authenticator. Using it deletes the row,
/// so each works once. Only its hash is stored, because nothing ever needs to show it again.
/// </summary>
internal sealed class RecoveryCode
{
    public required Guid UserId { get; init; }

    /// <summary>SHA-256 of the code as <see cref="RecoveryCodes.HashOf"/> normalizes it.</summary>
    public required byte[] CodeHash { get; init; }
}

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("recovery_codes");
        builder.HasKey(code => new { code.UserId, code.CodeHash });
        builder.Property(code => code.CodeHash).HasMaxLength(RecoveryCodes.HashLength);
        builder.HasOne<User>().WithMany().HasForeignKey(code => code.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
