using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Checks every stored key again, daily (FR-KEY-3): each key whose provider last answered a
/// check a day ago or longer, and each key that was never checked. See docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// It runs every hour, on the hour, and takes the keys that are due by then. So a key is checked
/// about a day after it was last checked, the keys of the service are not all checked at one
/// time of day, and a key whose provider gave no answer is tried again an hour later.
/// </para>
/// <para>
/// One key does not keep the others from being checked: what goes wrong with it is logged, and
/// the run goes on. A provider, or an environment of Binance, that gives no answer is left alone
/// for the rest of the run, so that a provider which is down is asked once and not once for
/// every user.
/// </para>
/// <para>
/// A run can end anywhere and be repeated: each key is written on its own, and a key that was
/// checked is not due again.
/// </para>
/// </remarks>
internal sealed class KeyValidationJob(
    IKeyValidation validation,
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<KeyValidationJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "key-validation";

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromHours(1));

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        var due = clock.GetUtcNow() - KeyValidator.Daily;

        // Which keys, and whose: read first, so that nothing of the database is held while a
        // provider is asked. The longest unchecked come first, and before them the never checked.
        List<(Guid UserId, AiProviderCode Provider)> aiKeys;
        List<(Guid UserId, TradingMode Environment)> exchangeKeys;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            aiKeys =
            [
                .. (await context.Set<AiCredential>()
                    .Where(credential => credential.ValidatedAt == null || credential.ValidatedAt <= due)
                    .OrderBy(credential => credential.ValidatedAt != null)
                    .ThenBy(credential => credential.ValidatedAt)
                    .Select(credential => new { credential.UserId, credential.Provider })
                    .ToListAsync(cancellationToken))
                .Select(key => (key.UserId, key.Provider)),
            ];

            exchangeKeys =
            [
                .. (await context.Set<ExchangeCredential>()
                    .Where(credential => credential.ValidatedAt == null || credential.ValidatedAt <= due)
                    .OrderBy(credential => credential.ValidatedAt != null)
                    .ThenBy(credential => credential.ValidatedAt)
                    .Select(credential => new { credential.UserId, credential.Environment })
                    .ToListAsync(cancellationToken))
                .Select(key => (key.UserId, key.Environment)),
            ];
        }

        var silentProviders = new HashSet<AiProviderCode>();
        foreach (var (userId, provider) in aiKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (silentProviders.Contains(provider))
            {
                continue;
            }

            // The owner is taken from the row, and everything logged for the key names them.
            using var owner = logger.BeginCorrelation(new Correlation(UserId: userId.ToString()));

            try
            {
                if (await validation.RevalidateAiKeyAsync(userId, provider, cancellationToken) == KeyCheckOutcome.Unreachable)
                {
                    silentProviders.Add(provider);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(
                    error, "The daily check of the key of user {UserId} for the AI provider {Provider} failed", userId, provider.Value);
            }
        }

        var silentEnvironments = new HashSet<TradingMode>();
        foreach (var (userId, environment) in exchangeKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (silentEnvironments.Contains(environment))
            {
                continue;
            }

            using var owner = logger.BeginCorrelation(new Correlation(UserId: userId.ToString()));

            try
            {
                if (await validation.RevalidateExchangeKeyAsync(userId, environment, cancellationToken) == KeyCheckOutcome.Unreachable)
                {
                    silentEnvironments.Add(environment);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(
                    error, "The daily check of the key of user {UserId} of Binance for {TradingMode} failed", userId, environment);
            }
        }
    }
}
