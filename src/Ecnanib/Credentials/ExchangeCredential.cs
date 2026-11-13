using System.Text;
using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Credentials;

/// <summary>
/// A user's Binance credential for one environment, as a row of the <c>exchange_credentials</c>
/// table (FR-KEY-2). It is an API key and its secret key, stored encrypted as one value, and
/// nothing of it is stored readable but the last four characters of the API key (FR-KEY-6,
/// FR-KEY-8). See docs/credentials.md.
/// </summary>
/// <param name="id">
/// Assigned by the application when the first credential for the environment is stored. It stays
/// the same when the credential is replaced, so that what points at it still does.
/// </param>
/// <param name="userId">The user whose credential it is.</param>
/// <param name="environment">Binance's test environment or the real exchange. A user has one credential for each.</param>
/// <param name="encryptedValue">The two keys, as <see cref="ValueOf"/> puts them together and <c>StoredSecrets</c> encrypted them.</param>
/// <param name="masterKeyId">The id of the master key of the configuration file that encrypted it.</param>
/// <param name="maskedSuffix">
/// The last four characters of the API key, which is all that is ever shown. Nothing of the
/// secret key is.
/// </param>
/// <param name="savedAt">When the credential that is stored now was stored.</param>
public sealed class ExchangeCredential(
    Guid id,
    Guid userId,
    TradingMode environment,
    byte[] encryptedValue,
    string masterKeyId,
    string maskedSuffix,
    DateTimeOffset savedAt) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public TradingMode Environment { get; } = environment;

    public byte[] EncryptedValue { get; } = encryptedValue;

    public string MasterKeyId { get; } = masterKeyId;

    public string MaskedSuffix { get; } = maskedSuffix;

    public DateTimeOffset SavedAt { get; } = savedAt;

    /// <summary>
    /// What the stored credential of a row is bound to, so that it decrypts in that row of that
    /// user only.
    /// </summary>
    public static string BoundTo(Guid id, Guid userId) => $"exchange-key:{userId}:{id}";

    /// <summary>
    /// The two keys as the one value that is encrypted: the API key, a line feed, and the secret
    /// key. Neither holds a line feed, so the first one in the value is where they part.
    /// </summary>
    /// <returns>The value. It is the caller's to encrypt and to clear.</returns>
    public static byte[] ValueOf(string apiKey, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        return Encoding.UTF8.GetBytes($"{apiKey}\n{secretKey}");
    }
}

internal sealed class ExchangeCredentialConfiguration : IEntityTypeConfiguration<ExchangeCredential>
{
    public void Configure(EntityTypeBuilder<ExchangeCredential> builder)
    {
        builder.ToTable("exchange_credentials");
        builder.HasKey(credential => credential.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(credential => credential.Id).ValueGeneratedNever();
        builder.Property(credential => credential.UserId);
        builder.Property(credential => credential.Environment);
        builder.Property(credential => credential.EncryptedValue);
        builder.Property(credential => credential.MasterKeyId);
        builder.Property(credential => credential.MaskedSuffix);
        builder.Property(credential => credential.SavedAt);

        // One credential for each environment. The index also finds the credentials of a user.
        builder.HasIndex(credential => new { credential.UserId, credential.Environment }).IsUnique();

        // The credentials belong to the account and go with it when it is deleted, at once (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
