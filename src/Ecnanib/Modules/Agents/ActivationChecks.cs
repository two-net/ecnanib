using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// FR-AGT-05's checks for a Demo agent, apart from the pair's slot (FR-AGT-10), which needs the caller's
/// lock. They read the user's credentials, the catalog and Binance's symbols, so they run before the caller
/// opens a transaction: a symbol lookup can call Binance, and a transaction is never held across an
/// exchange call. Switching to Live, with its own checks, is #39.
/// </summary>
internal sealed class ActivationChecks(ICredentials credentials, ICatalog catalog, IExchangeSymbols symbols)
{
    /// <summary>Everything that stops the agent being activated, and what's worth a warning.</summary>
    public async Task<(List<ActivationProblem> Problems, List<ActivationWarning> Warnings)> CheckAsync(
        Guid userId, AgentSettings settings, CancellationToken cancellationToken)
    {
        var problems = new List<ActivationProblem>();
        var warnings = new List<ActivationWarning>();

        if (settings.Environment != TradingEnvironment.Demo)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.LiveNotAvailable));
        }

        var keys = await credentials.ListAsync(userId, cancellationToken);

        if (settings.ExchangeCredentialId is not { } exchangeId)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.NoExchangeKey));
        }
        else if (ExchangeKeyProblem(keys.FirstOrDefault(key => key.Id == exchangeId), settings) is { } problem)
        {
            problems.Add(new ActivationProblem(problem));
        }

        if (await SymbolProblemAsync(settings.Market, settings.Environment, settings.Symbol, cancellationToken) is { } symbolProblem)
        {
            problems.Add(new ActivationProblem(symbolProblem));
        }

        await CheckRolesAsync(userId, settings, keys, problems, warnings, cancellationToken);

        if (settings.CapitalAllocation is not > 0m)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.NoCapitalAllocation));
        }

        if (settings.Risk.Problems().Count > 0)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.RiskSettingsInvalid));
        }

        return (problems, warnings);
    }

    /// <summary>What's wrong with the agent's exchange key, or null. A key of the wrong kind or environment counts as missing.</summary>
    public static ActivationProblemKind? ExchangeKeyProblem(CredentialRecord? key, AgentSettings settings) =>
        key is null || key.Kind != CredentialKind.Exchange || key.Environment != settings.Environment ? ActivationProblemKind.ExchangeKeyMissing
        : key.Status != CredentialStatus.Valid ? ActivationProblemKind.ExchangeKeyNotValid
        : !EnabledFor(key, settings.Market) ? ActivationProblemKind.MarketNotEnabled
        : null;

    public static bool EnabledFor(CredentialRecord key, Market market) =>
        (market == Market.Spot ? key.SpotEnabled : key.FuturesEnabled) == true;

    /// <summary>
    /// What's wrong with the pair, or null when Binance lists it as trading, and as a perpetual contract on
    /// Futures. <paramref name="symbol"/> is the cleaned pair.
    /// </summary>
    public async Task<ActivationProblemKind?> SymbolProblemAsync(
        Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken) =>
        (await FindSymbolAsync(market, environment, symbol, cancellationToken)).Problem;

    /// <summary>Binance's record of the pair, or what's wrong with it.</summary>
    public async Task<(ExchangeSymbolInfo? Symbol, ActivationProblemKind? Problem)> FindSymbolAsync(
        Market market, TradingEnvironment environment, string symbol, CancellationToken cancellationToken)
    {
        ExchangeSymbolInfo? info;

        try
        {
            info = await symbols.FindAsync(market, environment, symbol, cancellationToken);
        }
        catch (MarketDataException)
        {
            return (null, ActivationProblemKind.SymbolUnverifiable);
        }

        return info switch
        {
            null => (null, ActivationProblemKind.SymbolUnknown),
            { IsTrading: false } => (info, ActivationProblemKind.SymbolNotTrading),
            { Market: Market.Futures } when info.ContractType != FuturesContract.Perpetual => (info, ActivationProblemKind.SymbolNotPerpetual),
            _ => (info, null),
        };
    }

    /// <summary>
    /// Each required role needs a model that's Active in the catalog and that one of the user's valid AI keys
    /// can use. Validity comes from Credentials, not the catalog's rows, which follow a key's change a moment
    /// later. An optional role's model and every fallback are checked the same way, but only warn.
    /// </summary>
    private async Task CheckRolesAsync(
        Guid userId,
        AgentSettings settings,
        IReadOnlyList<CredentialRecord> keys,
        List<ActivationProblem> problems,
        List<ActivationWarning> warnings,
        CancellationToken cancellationToken)
    {
        if (settings.Roles.Count == 0)
        {
            problems.AddRange(AgentRoles.Required.Select(role => new ActivationProblem(ActivationProblemKind.RoleUnassigned, role)));
            return;
        }

        var models = (await catalog.ListAsync(cancellationToken)).ToDictionary(model => new ModelRef(model.Provider, model.ModelId));
        var validKeys = keys.Where(key => key is { Kind: CredentialKind.Ai, Status: CredentialStatus.Valid }).Select(key => key.Id).ToHashSet();
        var reachable = (await catalog.ListAccessAsync(userId, cancellationToken))
            .Where(access => access.Available && validKeys.Contains(access.CredentialId))
            .Select(access => new ModelRef(access.Provider, access.ModelId))
            .ToHashSet();

        ActivationProblemKind? ModelProblem(ModelRef model) =>
            !models.TryGetValue(model, out var listed) || !reachable.Contains(model) ? ActivationProblemKind.ModelNotAccessible
            : listed.Status != ModelStatus.Active ? ActivationProblemKind.ModelNotActive
            : null;

        foreach (var role in Enum.GetValues<AiRole>())
        {
            var assignment = settings.RoleOf(role);

            if (assignment is null)
            {
                if (AgentRoles.IsRequired(role))
                {
                    problems.Add(new ActivationProblem(ActivationProblemKind.RoleUnassigned, role));
                }

                continue;
            }

            if (ModelProblem(assignment.Model) is { } problem)
            {
                if (AgentRoles.IsRequired(role))
                {
                    problems.Add(new ActivationProblem(problem, role));
                }
                else
                {
                    warnings.Add(new ActivationWarning(ActivationWarningKind.OptionalModelUnavailable, role));
                }
            }

            if (assignment.Fallback is { } fallback && ModelProblem(fallback) is not null)
            {
                warnings.Add(new ActivationWarning(ActivationWarningKind.FallbackUnavailable, role));
            }
        }
    }

    private static class FuturesContract
    {
        /// <summary>As Binance names it. A delivery contract has no funding and expires.</summary>
        public const string Perpetual = "PERPETUAL";
    }
}
