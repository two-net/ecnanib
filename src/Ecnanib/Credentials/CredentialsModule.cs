using Ecnanib.Accounts;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Security;

namespace Ecnanib.Credentials;

/// <summary>Credentials: BYOK storage, encryption, validation (SRS 3.1). See docs/credentials.md.</summary>
public static class CredentialsModule
{
    /// <summary>
    /// Registers the component. What depends on a key is told when the key goes, or turns out
    /// to be invalid, through <see cref="IKeyDependents"/>, which Agents implements and registers.
    /// </summary>
    public static IServiceCollection AddCredentials(this IServiceCollection services)
    {
        services.AddSingleton<IStoredKeys, StoredKeys>();

        // What checks a stored key again with its provider, and what Agents asks about a key
        // before an agent starts (FR-KEY-3, FR-KEY-5). The job checks every key daily.
        services.AddSingleton<IKeyValidation, KeyValidator>();
        services.AddBackgroundJob<KeyValidationJob>();

        // What hands a stored key to the call of a model, decrypted for that call alone
        // (FR-KEY-9). What calls a model for an agent asks it, and nothing else does.
        services.AddSingleton<IKeyHandOver, KeyHandOver>();

        // One object keeps which models a key can use, for what stores a key and for what
        // checks one again, and says from that which models a user can select (FR-CAT-9).
        services.AddSingleton<KeyModels>();
        services.AddSingleton<IKeyModels>(provider => provider.GetRequiredService<KeyModels>());

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

        // What syncs the model catalog for an operator, outside its schedule, and records that
        // it was asked for (FR-CAT-2, FR-OPS-4). It is here because of what it uses: the Catalog
        // may not use Accounts, which keeps the audit trail, and this component may use both.
        services.AddSingleton<IOperatorCatalogSync, OperatorCatalogSync>();

        // The parts of the export of a user's data that this component keeps: which keys are
        // stored, each by its last four characters (NFR-SEC-9).
        services.AddSingleton<IExportedPart, AiKeysExport>();
        services.AddSingleton<IExportedPart, ExchangeKeysExport>();

        return services;
    }
}
