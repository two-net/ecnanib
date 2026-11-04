using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// A user's key for an AI provider or Binance (SRS 6.1). The secrets are rows in <c>secrets</c>, and this
/// keeps only their IDs, so listing credentials never decrypts anything (FR-KEY-03). There's no foreign key
/// to <c>users</c>, because another module owns that table: account deletion reaches this one through the
/// port Identity declares (#51). See Credentials in docs/architecture.md.
/// </summary>
internal sealed class Credential
{
    /// <summary>The longest enum name stored here, with room to spare.</summary>
    public const int NameMaxLength = 32;

    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required CredentialKind Kind { get; init; }

    /// <summary>An AI adapter's name, or <see cref="CredentialProviders.Binance"/>.</summary>
    public required string Provider { get; init; }

    /// <summary>Binance only (FR-KEY-02).</summary>
    public TradingEnvironment? Environment { get; init; }

    /// <summary>Binance only (FR-KEY-06).</summary>
    public BinanceKeyType? KeyType { get; init; }

    public required string Label { get; init; }

    /// <summary>The row in <c>secrets</c> holding the API key. Nothing else refers to it, so this owner deletes it.</summary>
    public required Guid ApiKeyId { get; init; }

    /// <summary>Binance only: the row in <c>secrets</c> holding the HMAC secret or the Ed25519 private key.</summary>
    public Guid? ApiSecretId { get; init; }

    /// <summary>The API key's last four characters (FR-KEY-03). For Binance, the API key's, never the secret's.</summary>
    public required string LastFour { get; init; }

    public required CredentialStatus Status { get; init; }

    public CredentialProblem? Problem { get; init; }

    public bool? SpotEnabled { get; init; }

    public bool? FuturesEnabled { get; init; }

    /// <summary>Binance Live only. Null where Binance doesn't say, which is Demo.</summary>
    public bool? IpRestricted { get; init; }

    /// <summary>Binance's ID for the account the key belongs to, so a rotation can't move to another account.</summary>
    public long? ExchangeUid { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a check last got an answer from the provider.</summary>
    public DateTimeOffset? ValidatedAt { get; init; }

    /// <summary>When a check last started, answered or not, which limits checks on demand to one a minute.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public CredentialRecord ToRecord() => new(
        Id,
        Kind,
        Provider,
        Environment,
        KeyType,
        Label,
        LastFour,
        Status,
        Problem,
        SpotEnabled,
        FuturesEnabled,
        IpRestricted,
        CreatedAt,
        ValidatedAt,
        LastUsedAt);
}

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("credentials");
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Kind).HasConversion<string>().HasMaxLength(Credential.NameMaxLength);
        builder.Property(credential => credential.Provider).HasMaxLength(Credential.NameMaxLength);
        builder.Property(credential => credential.Environment).HasConversion<string>().HasMaxLength(Credential.NameMaxLength);
        builder.Property(credential => credential.KeyType).HasConversion<string>().HasMaxLength(Credential.NameMaxLength);
        builder.Property(credential => credential.Label).HasMaxLength(CredentialLimits.LabelMaxLength);
        builder.Property(credential => credential.LastFour).HasMaxLength(4);
        builder.Property(credential => credential.Status).HasConversion<string>().HasMaxLength(Credential.NameMaxLength);
        builder.Property(credential => credential.Problem).HasConversion<string>().HasMaxLength(Credential.NameMaxLength);
        builder.HasIndex(credential => credential.UserId);
    }
}
