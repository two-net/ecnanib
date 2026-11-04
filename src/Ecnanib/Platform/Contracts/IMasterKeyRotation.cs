namespace Ecnanib.Platform.Contracts;

/// <summary>
/// How far re-encryption has got since the active master key changed (FR-KEY-08), for the system
/// health page (FR-ADM-04). It holds counts and key IDs only. See Secrets in docs/architecture.md.
/// </summary>
public interface IMasterKeyRotation
{
    Task<MasterKeyProgress> GetProgressAsync(CancellationToken cancellationToken);
}

/// <param name="ActiveKeyId">The key that encrypts every new secret.</param>
/// <param name="ActiveSince">When this key last became active, or null before the first start recorded it.</param>
/// <param name="SecretsByKeyId">How many secrets each key encrypts, including the active key. A key that encrypts none isn't listed.</param>
public sealed record MasterKeyProgress(
    string ActiveKeyId,
    DateTimeOffset? ActiveSince,
    IReadOnlyDictionary<string, long> SecretsByKeyId)
{
    public long Total => SecretsByKeyId.Values.Sum();

    /// <summary>Secrets that an older key still encrypts. Every older key they use must stay configured.</summary>
    public long Pending => Total - SecretsByKeyId.GetValueOrDefault(ActiveKeyId);

    /// <summary>True once no secret needs an older key, so older keys can be removed from the configuration.</summary>
    public bool Complete => Pending == 0;
}
