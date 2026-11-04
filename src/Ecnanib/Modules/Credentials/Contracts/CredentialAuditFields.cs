namespace Ecnanib.Modules.Credentials.Contracts;

/// <summary>
/// The field names in a credential's audit summary (FR-AUD-02), which the Web UI gives text to. None looks
/// like a secret's name, which would mask its values. Records store them, so a value is never renamed.
/// </summary>
public static class CredentialAuditFields
{
    /// <summary>An AI provider's name, or Binance.</summary>
    public const string Provider = nameof(Provider);

    /// <summary>A <see cref="Kernel.TradingEnvironment"/> name, for Binance.</summary>
    public const string Environment = nameof(Environment);

    public const string Label = nameof(Label);

    /// <summary>A <see cref="CredentialStatus"/> name.</summary>
    public const string Status = nameof(Status);

    /// <summary>The last four characters of the API key, which FR-KEY-03 shows anyway.</summary>
    public const string Ending = nameof(Ending);
}
