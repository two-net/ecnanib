using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Keeps, with a user's key for an AI provider, which models the provider listed to it, and says
/// from that which models the user can select (FR-CAT-9). See docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// What is kept is the provider's identifiers, as the catalog was told them when the provider
/// took the key. They are held against the catalog when someone asks, by provider and
/// identifier, so a model that the catalog comes to hold later is offered without the key being
/// checked again, and a model that is gone from the catalog is not.
/// </para>
/// <para>
/// A list is written only where the row still holds the key that it was asked with. The
/// provider is asked with no transaction open, and a key that was replaced or deleted meanwhile
/// never gets what was listed to the one before it. A key that is replaced starts with nothing
/// known, in the statement that stores it.
/// </para>
/// </remarks>
internal sealed class KeyModels(
    ISelectableModels selectable,
    IDbContextFactory<EcnanibDbContext> contexts,
    ILogger<KeyModels> logger) : IKeyModels
{
    public async Task<IReadOnlyList<ProviderModels>> SelectableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var models = await selectable.ListAsync(cancellationToken);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The providers are the rows of their table, and not a list of this code.
        var providers = await context.Set<AiProvider>()
            .OrderBy(provider => provider.Code)
            .Select(provider => provider.Code)
            .ToListAsync(cancellationToken);

        // Only what says which models a key can use. The encrypted keys are not read.
        var keys = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Select(credential => new StoredList(credential.Provider, credential.Status, credential.AccessibleModels))
            .ToListAsync(cancellationToken);

        return
        [
            .. providers.Select(provider =>
            {
                var key = keys.SingleOrDefault(stored => stored.Provider == provider);
                if (BlockOf(key) is { } block)
                {
                    return new ProviderModels(provider, block, []);
                }

                var listed = new HashSet<string>(key!.AccessibleModels!, StringComparer.Ordinal);

                return new ProviderModels(
                    provider,
                    Block: null,
                    [.. models.Where(model => model.Provider == provider && listed.Contains(model.Identifier))]);
            }),
        ];
    }

    public async Task<ModelBlock?> BlockForAsync(Guid userId, Guid modelId, CancellationToken cancellationToken)
    {
        if (await selectable.FindAsync(modelId, cancellationToken) is not { } model)
        {
            return ModelBlock.NotSelectable;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var key = await context.Set<AiCredential>()
            .OwnedBy(userId)
            .Where(credential => credential.Provider == model.Provider)
            .Select(credential => new StoredList(credential.Provider, credential.Status, credential.AccessibleModels))
            .SingleOrDefaultAsync(cancellationToken);

        if (BlockOf(key) is { } block)
        {
            return block;
        }

        return key!.AccessibleModels!.Contains(model.Identifier, StringComparer.Ordinal) ? null : ModelBlock.NotListed;
    }

    /// <summary>
    /// Keeps what a provider listed to a key, with the key. It is called after the catalog was
    /// told of the key, with no transaction open, and never fails for anything but the caller's
    /// cancelling: the key is stored and valid by then, whatever becomes of the list.
    /// </summary>
    /// <param name="userId">The user whose key it is.</param>
    /// <param name="provider">The provider the key is for.</param>
    /// <param name="keyId">The row of the key.</param>
    /// <param name="encryptedValue">What the row held when the key was read, or was stored.</param>
    /// <param name="listed">
    /// What the catalog answered: the identifiers that the provider listed to the key, or null
    /// when it gave no list. Null writes nothing, so what was known of the key stays known.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task RecordAsync(
        Guid userId,
        AiProviderCode provider,
        Guid keyId,
        byte[] encryptedValue,
        IReadOnlyList<string>? listed,
        CancellationToken cancellationToken)
    {
        if (listed is null)
        {
            return;
        }

        var identifiers = listed.ToArray();

        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);

            // In one statement, and only where the row still holds the key that was asked with:
            // a key that took its place meanwhile was listed nothing yet.
            await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Id == keyId && credential.EncryptedValue == encryptedValue)
                .ExecuteUpdateAsync(
                    row => row.SetProperty(credential => credential.AccessibleModels, identifiers), cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The key is stored and valid. What it can use is asked again when it is next checked.
            logger.LogError(
                error,
                "The models that the AI provider {Provider} listed to the key of user {UserId} could not be recorded. They are asked again when the key is next checked",
                provider.Value,
                userId);
        }
    }

    /// <summary>What about a key keeps its user from selecting any model of its provider, or null when the key can be used.</summary>
    private static ModelBlock? BlockOf(StoredList? key) => key switch
    {
        null => ModelBlock.NoKey,
        { Status: not KeyStatus.Valid } => ModelBlock.KeyNotValid,
        { AccessibleModels: null } => ModelBlock.NotKnown,
        _ => null,
    };

    /// <summary>What is read of a key to say which models it can use.</summary>
    private sealed record StoredList(AiProviderCode Provider, KeyStatus Status, string[]? AccessibleModels);
}
