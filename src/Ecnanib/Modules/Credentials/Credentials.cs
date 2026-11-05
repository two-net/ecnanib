using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Adding, validating, rotating and deleting a user's keys (FR-KEY-01 to FR-KEY-05). Every query filters on
/// the user (NFR-SEC-04). A secret commits with the row that refers to it, and an audit record with the
/// change it describes. See Credentials in docs/architecture.md.
/// </summary>
internal sealed class Credentials(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ISecretStore secrets,
    IAuditTrail audit,
    ITwoFactor twoFactor,
    IAiProviders aiProviders,
    AiValidator aiValidator,
    BinanceValidator binanceValidator,
    CredentialChecks checks,
    CredentialSettings settings,
    TimeProvider time,
    CredentialObservers observers,
    ILogger<Credentials> logger) : ICredentials
{
    /// <summary>What a credential's audit records name as their target.</summary>
    public const string Target = "Credential";

    public IReadOnlyList<string> AiProviders =>
        [.. aiProviders.All.Where(provider => provider.Enabled).Select(provider => provider.Name)];

    public IReadOnlyList<string> EgressIpAddresses => settings.EgressIpAddresses;

    public async Task<IReadOnlyList<CredentialRecord>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.Set<Credential>()
            .AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => row.ToRecord())];
    }

    public async Task<CredentialRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return (await FindRowAsync(context, userId, id, cancellationToken))?.ToRecord();
    }

    public async Task<SaveResult> AddAiKeyAsync(
        Guid userId, string provider, string label, SecretValue apiKey, CancellationToken cancellationToken)
    {
        if (!AiProviders.Contains(provider, StringComparer.Ordinal))
        {
            return new SaveResult(SaveOutcome.UnknownProvider);
        }

        if (KeyInput.Label(label) is not { } cleaned)
        {
            return new SaveResult(SaveOutcome.LabelInvalid);
        }

        if (!KeyInput.IsKey(apiKey))
        {
            return new SaveResult(SaveOutcome.KeyMalformed);
        }

        var id = Guid.NewGuid();
        var verdict = await aiValidator.ValidateAsync(provider, id, apiKey, cancellationToken);

        if (Refusal(verdict) is { } refusal)
        {
            logger.LogInformation("Refused a {Provider} key for user {UserId}: {Problem}", provider, userId, verdict.Problem);
            return refusal;
        }

        return await AddAsync(userId, id, CredentialKind.Ai, provider, environment: null, keyType: null, cleaned, verdict, apiKey, secret: null, cancellationToken);
    }

    public async Task<SaveResult> AddBinanceKeyAsync(
        Guid userId,
        TradingEnvironment environment,
        string label,
        BinanceKeyType keyType,
        SecretValue apiKey,
        SecretValue secret,
        string? twoFactorCode,
        CancellationToken cancellationToken)
    {
        if (KeyInput.Label(label) is not { } cleaned)
        {
            return new SaveResult(SaveOutcome.LabelInvalid);
        }

        if (!KeyInput.IsKey(apiKey) || !KeyInput.IsSecret(keyType, secret))
        {
            return new SaveResult(SaveOutcome.KeyMalformed);
        }

        // Before Binance hears of the key, so that a stolen session can't use the server to try keys (FR-AUTH-05).
        var check = await twoFactor.CheckAsync(userId, twoFactorCode, cancellationToken);

        if (!check.Allows())
        {
            return new SaveResult(SaveOutcome.TwoFactorRefused, TwoFactor: check);
        }

        var id = Guid.NewGuid();
        var verdict = await binanceValidator.ValidateAsync(id, environment, keyType, apiKey, secret, cancellationToken);

        if (ExchangeRefusal(verdict) is { } refusal)
        {
            logger.LogInformation(
                "Refused a Binance {Environment} key for user {UserId}: {Outcome} {Problem}", environment, userId, refusal.Outcome, verdict.Problem);
            return refusal;
        }

        return await AddAsync(userId, id, CredentialKind.Exchange, CredentialProviders.Binance, environment, keyType, cleaned, verdict, apiKey, secret, cancellationToken);
    }

    public async Task<SaveResult> RotateAiKeyAsync(Guid userId, Guid id, SecretValue apiKey, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, id, cancellationToken);

        if (row is not { Kind: CredentialKind.Ai })
        {
            return new SaveResult(SaveOutcome.NotFound);
        }

        if (!KeyInput.IsKey(apiKey))
        {
            return new SaveResult(SaveOutcome.KeyMalformed);
        }

        var verdict = await aiValidator.ValidateAsync(row.Provider, row.Id, apiKey, cancellationToken);

        if (Refusal(verdict) is { } refusal)
        {
            logger.LogInformation("Refused a new {Provider} key for credential {CredentialId}: {Problem}", row.Provider, row.Id, verdict.Problem);
            return refusal;
        }

        return await RotateAsync(row, keyType: null, verdict, apiKey, secret: null, cancellationToken);
    }

    public async Task<SaveResult> RotateBinanceKeyAsync(
        Guid userId,
        Guid id,
        BinanceKeyType keyType,
        SecretValue apiKey,
        SecretValue secret,
        string? twoFactorCode,
        CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, id, cancellationToken);

        if (row is not { Kind: CredentialKind.Exchange, Environment: { } environment })
        {
            return new SaveResult(SaveOutcome.NotFound);
        }

        if (!KeyInput.IsKey(apiKey) || !KeyInput.IsSecret(keyType, secret))
        {
            return new SaveResult(SaveOutcome.KeyMalformed);
        }

        var check = await twoFactor.CheckAsync(userId, twoFactorCode, cancellationToken);

        if (!check.Allows())
        {
            return new SaveResult(SaveOutcome.TwoFactorRefused, TwoFactor: check);
        }

        var verdict = await binanceValidator.ValidateAsync(row.Id, environment, keyType, apiKey, secret, cancellationToken);

        if (ExchangeRefusal(verdict) is { } refusal)
        {
            logger.LogInformation(
                "Refused a new Binance key for credential {CredentialId}: {Outcome} {Problem}", row.Id, refusal.Outcome, verdict.Problem);
            return refusal;
        }

        // Agents' orders and positions live on the account, so a rotation can't move to another one. When
        // Binance didn't say which account the new key is for, that can't be shown.
        if (row.ExchangeUid is { } uid && verdict.Uid != uid)
        {
            logger.LogInformation("Refused a new Binance key for credential {CredentialId}: another account", row.Id);
            return new SaveResult(SaveOutcome.DifferentAccount);
        }

        return await RotateAsync(row, keyType, verdict, apiKey, secret, cancellationToken);
    }

    public async Task<RenameOutcome> RenameAsync(Guid userId, Guid id, string label, CancellationToken cancellationToken)
    {
        if (KeyInput.Label(label) is not { } cleaned)
        {
            return RenameOutcome.LabelInvalid;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Not audited: FR-AUD-01 doesn't list it, as it doesn't list preferences.
        var renamed = await context.Set<Credential>()
            .Where(row => row.Id == id && row.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Label, cleaned), cancellationToken);

        return renamed == 1 ? RenameOutcome.Renamed : RenameOutcome.NotFound;
    }

    public Task<ValidationResult> ValidateAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        checks.CheckAsync(userId, id, AuditActorKind.User, cancellationToken);

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var row = await FindRowAsync(context, userId, id, cancellationToken);

        if (row is null)
        {
            return false;
        }

        // Conditional on the key it read, so a rotation meanwhile can't leave its new secrets behind.
        var deleted = await context.Set<Credential>()
            .Where(current => current.Id == row.Id && current.UserId == userId && current.ApiKeyId == row.ApiKeyId)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            return false;
        }

        await RemoveSecretsAsync(context, row, cancellationToken);

        audit.Add(context, Entry(AuditAction.CredentialDeleted, row, Described(row, before: true)));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Deleted credential {CredentialId} for user {UserId}: {Provider}", row.Id, userId, row.Provider);

        observers.Notify(row, CredentialChangeKind.Deleted, status: null, row.Status);

        return true;
    }

    private async Task<SaveResult> AddAsync(
        Guid userId,
        Guid id,
        CredentialKind kind,
        string provider,
        TradingEnvironment? environment,
        BinanceKeyType? keyType,
        string label,
        Verdict verdict,
        SecretValue apiKey,
        SecretValue? secret,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = new Credential
        {
            Id = id,
            UserId = userId,
            Kind = kind,
            Provider = provider,
            Environment = environment,
            KeyType = keyType,
            Label = label,
            ApiKeyId = secrets.Add(context, apiKey),
            ApiSecretId = secret is null ? null : secrets.Add(context, secret),
            LastFour = KeyInput.LastFour(apiKey),
            Status = verdict.Reached ? verdict.Status : CredentialStatus.Unverified,
            Problem = verdict.Problem,
            SpotEnabled = verdict.SpotEnabled,
            FuturesEnabled = verdict.FuturesEnabled,
            IpRestricted = verdict.IpRestricted,
            ExchangeUid = verdict.Uid,
            CreatedAt = now,
            ValidatedAt = verdict.Reached ? now : null,
            CheckedAt = now,
        };

        context.Set<Credential>().Add(row);
        audit.Add(context, Entry(AuditAction.CredentialAdded, row, Described(row, before: false)));

        // One save, so the secrets, the row and the record commit together or not at all.
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Added credential {CredentialId} for user {UserId}: {Provider} {Environment} {Status} {Problem}",
            row.Id, userId, provider, environment, row.Status, row.Problem);

        observers.Notify(row, CredentialChangeKind.Added, row.Status, previous: null);

        return new SaveResult(SaveOutcome.Saved, row.ToRecord());
    }

    private async Task<SaveResult> RotateAsync(
        Credential row,
        BinanceKeyType? keyType,
        Verdict verdict,
        SecretValue apiKey,
        SecretValue? secret,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var status = verdict.Reached ? verdict.Status : CredentialStatus.Unverified;
        var lastFour = KeyInput.LastFour(apiKey);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var apiKeyId = secrets.Add(context, apiKey);
        var apiSecretId = secret is null ? (Guid?)null : secrets.Add(context, secret);

        // Conditional on the key it replaces, so of two rotations at once only one wins, and a deletion
        // meanwhile isn't undone.
        var updated = await context.Set<Credential>()
            .Where(current => current.Id == row.Id && current.UserId == row.UserId && current.ApiKeyId == row.ApiKeyId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(current => current.ApiKeyId, apiKeyId)
                    .SetProperty(current => current.ApiSecretId, apiSecretId)
                    .SetProperty(current => current.KeyType, keyType ?? row.KeyType)
                    .SetProperty(current => current.LastFour, lastFour)
                    .SetProperty(current => current.Status, status)
                    .SetProperty(current => current.Problem, verdict.Problem)
                    .SetProperty(current => current.SpotEnabled, verdict.SpotEnabled)
                    .SetProperty(current => current.FuturesEnabled, verdict.FuturesEnabled)
                    .SetProperty(current => current.IpRestricted, verdict.IpRestricted)
                    .SetProperty(current => current.ExchangeUid, verdict.Uid ?? row.ExchangeUid)
                    .SetProperty(current => current.ValidatedAt, verdict.Reached ? now : null)
                    .SetProperty(current => current.CheckedAt, now),
                cancellationToken);

        if (updated == 0)
        {
            // Disposing the transaction rolls it back, and the new secrets were never saved.
            return new SaveResult(SaveOutcome.Changed);
        }

        await RemoveSecretsAsync(context, row, cancellationToken);

        audit.Add(
            context,
            Entry(
                AuditAction.CredentialRotated,
                row,
                [
                    new(CredentialAuditFields.Ending, row.LastFour, lastFour),
                    new(CredentialAuditFields.Status, row.Status.ToString(), status.ToString()),
                ]));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Rotated credential {CredentialId} for user {UserId}: {Provider} {Status} {Problem}", row.Id, row.UserId, row.Provider, status, verdict.Problem);

        observers.Notify(row, CredentialChangeKind.Rotated, status, row.Status);

        var current = await FindRowAsync(context, row.UserId, row.Id, cancellationToken);

        return current is null ? new SaveResult(SaveOutcome.Changed) : new SaveResult(SaveOutcome.Saved, current.ToRecord());
    }

    private async Task RemoveSecretsAsync(EcnanibDbContext context, Credential row, CancellationToken cancellationToken)
    {
        await secrets.RemoveAsync(context, row.ApiKeyId, cancellationToken);

        if (row.ApiSecretId is { } secretId)
        {
            await secrets.RemoveAsync(context, secretId, cancellationToken);
        }
    }

    private async Task<Credential?> FindRowAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await FindRowAsync(context, userId, id, cancellationToken);
    }

    private static Task<Credential?> FindRowAsync(EcnanibDbContext context, Guid userId, Guid id, CancellationToken cancellationToken) =>
        context.Set<Credential>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);

    /// <summary>An AI key is refused only when the provider refused it. One it couldn't ask about is stored as Unverified.</summary>
    private static SaveResult? Refusal(Verdict verdict) => verdict.Unacceptable ? new SaveResult(SaveOutcome.Refused) : null;

    /// <summary>
    /// A Binance key is stored only once Binance has said what it may do, so a key that can withdraw is
    /// never at rest here, even for a while (AC-04).
    /// </summary>
    private static SaveResult? ExchangeRefusal(Verdict verdict) => verdict switch
    {
        { Reached: false } => new SaveResult(SaveOutcome.Unreachable),
        { Problem: CredentialProblem.WithdrawalsEnabled } => new SaveResult(SaveOutcome.WithdrawalsEnabled),
        { Unacceptable: true } => new SaveResult(SaveOutcome.Refused),
        _ => null,
    };

    private static AuditEntry Entry(AuditAction action, Credential row, IReadOnlyList<AuditChange> changes) =>
        AuditEntry.ByUser(action, row.UserId) with { TargetType = Target, TargetId = row.Id.ToString(), Changes = changes };

    /// <summary>What the credential is, as it was before a deletion or as it is after an addition.</summary>
    private static List<AuditChange> Described(Credential row, bool before)
    {
        List<(string Field, string? Value)> fields =
        [
            (CredentialAuditFields.Provider, row.Provider),
            (CredentialAuditFields.Environment, row.Environment?.ToString()),
            (CredentialAuditFields.Label, row.Label),
            (CredentialAuditFields.Status, row.Status.ToString()),
        ];

        return
        [
            .. fields
                .Where(field => field.Value is not null)
                .Select(field => before ? new AuditChange(field.Field, field.Value, null) : new AuditChange(field.Field, null, field.Value)),
        ];
    }
}
