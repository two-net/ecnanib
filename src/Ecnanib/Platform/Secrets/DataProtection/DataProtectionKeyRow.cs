using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Secrets.DataProtection;

/// <summary>
/// One ASP.NET Data Protection key, which protects cookies and antiforgery tokens. Its key material
/// isn't here: the XML refers to a row in <c>secrets</c> instead, so it's encrypted with the master key.
/// </summary>
internal sealed class DataProtectionKeyRow
{
    public long Id { get; init; }

    public string? FriendlyName { get; init; }

    public required string Xml { get; init; }
}

internal sealed class DataProtectionKeyRowConfiguration : IEntityTypeConfiguration<DataProtectionKeyRow>
{
    public void Configure(EntityTypeBuilder<DataProtectionKeyRow> builder)
    {
        builder.ToTable("data_protection_keys");
        builder.HasKey(row => row.Id);
    }
}
