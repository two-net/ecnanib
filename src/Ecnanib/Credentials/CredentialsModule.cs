using Ecnanib.Accounts;
using Ecnanib.Platform.Security;

namespace Ecnanib.Credentials;

/// <summary>Credentials: BYOK storage, encryption, validation (SRS 3.1). See docs/credentials.md.</summary>
public static class CredentialsModule
{
    /// <summary>
    /// Registers the component. What depends on a key is told when the key goes through
    /// <see cref="IKeyDependents"/>, which Agents implements and registers.
    /// </summary>
    public static IServiceCollection AddCredentials(this IServiceCollection services)
    {
        services.AddSingleton<IStoredKeys, StoredKeys>();

        // The keys are encrypted by the platform's StoredSecrets, and the columns that hold them
        // are declared, so that each key is encrypted again when the active master key has
        // changed (FR-KEY-7).
        services.AddStoredSecret<AiCredential>(
            credential => credential.EncryptedValue,
            credential => credential.MasterKeyId,
            credential => AiCredential.BoundTo(credential.Id, credential.UserId));
        services.AddStoredSecret<ExchangeCredential>(
            credential => credential.EncryptedValue,
            credential => credential.MasterKeyId,
            credential => ExchangeCredential.BoundTo(credential.Id, credential.UserId));

        // The parts of the export of a user's data that this component keeps: which keys are
        // stored, each by its last four characters (NFR-SEC-9).
        services.AddSingleton<IExportedPart, AiKeysExport>();
        services.AddSingleton<IExportedPart, ExchangeKeysExport>();

        return services;
    }
}
