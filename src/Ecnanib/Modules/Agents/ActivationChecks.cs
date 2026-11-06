using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// FR-AGT-05's checks for an agent in its environment, apart from the pair's slot (FR-AGT-10), which needs the
/// caller's lock, and EXT-BIN-07's for a Futures agent. They read the user's credentials, the catalog, Binance's
/// symbols and the Futures account, so they run before the caller opens a transaction: a symbol lookup or an
/// account read can call Binance, and a transaction is never held across an exchange call. Switching to Live runs
/// them on the Live settings, with its own checks on top (<see cref="Agents.SwitchToLiveAsync"/>).
/// </summary>
internal sealed class ActivationChecks(
    ICredentials credentials,
    RoleModels roleModels,
    IExchangeSymbols symbols,
    IAgentExposure exposure,
    RiskCaps caps)
{
    /// <summary>Everything that stops the agent being activated, and what's worth a warning.</summary>
    public async Task<(List<ActivationProblem> Problems, List<ActivationWarning> Warnings)> CheckAsync(
        Guid userId, AgentSettings settings, CancellationToken cancellationToken)
    {
        var problems = new List<ActivationProblem>();
        var warnings = new List<ActivationWarning>();

        var keys = await credentials.ListAsync(userId, cancellationToken);

        var keyUsable = false;

        if (settings.ExchangeCredentialId is not { } exchangeId)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.NoExchangeKey));
        }
        else if (ExchangeKeyProblem(keys.FirstOrDefault(key => key.Id == exchangeId), settings) is { } problem)
        {
            problems.Add(new ActivationProblem(problem));
        }
        else
        {
            keyUsable = true;
        }

        var (symbol, symbolProblem) = await FindSymbolAsync(settings.Market, settings.Environment, settings.Symbol, cancellationToken);

        if (symbolProblem is not null)
        {
            problems.Add(new ActivationProblem(symbolProblem.Value));
        }

        // EXT-BIN-07 asks Binance about the account, which is only worth it once the key and the pair are good.
        if (settings.Market == Market.Futures && keyUsable && symbolProblem is null && symbol is not null)
        {
            problems.AddRange(await FuturesProblemsAsync(userId, settings, symbol, cancellationToken));
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

        // The caps may have been tightened since the agent was saved (FR-RISK-11).
        if (caps.Problems(settings.Risk).Count > 0)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.RiskSettingsAboveCaps));
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
    /// Binance's record of the pair, and what's wrong with it, which is null when Binance lists it as trading, and
    /// as a perpetual contract on Futures. <paramref name="symbol"/> is the cleaned pair.
    /// </summary>
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
    /// EXT-BIN-07's prerequisites, now, from the account behind the agent's key: Futures enabled, One-way Mode, and
    /// margin enough for the smallest position Binance accepts for the pair, which is its minimum notional over the
    /// agent's leverage after the caps. More capital than the account has is FR-AGT-13's warning, not a problem.
    /// Multi-Assets Mode is refused too, because it allows cross margin only, and FR-RISK-09 sets isolated before
    /// every entry.
    /// </summary>
    private async Task<List<ActivationProblem>> FuturesProblemsAsync(
        Guid userId, AgentSettings settings, ExchangeSymbolInfo symbol, CancellationToken cancellationToken)
    {
        var read = await exposure.ReadFuturesAccountAsync(userId, settings.ExchangeCredentialId!.Value, symbol.QuoteAsset, cancellationToken);

        if (read.State is not { } account)
        {
            return
            [
                new ActivationProblem(read.Failure == FuturesAccountFailure.KeyRefused
                    ? ActivationProblemKind.MarketNotEnabled
                    : ActivationProblemKind.FuturesAccountUnverifiable),
            ];
        }

        if (!account.CanTrade)
        {
            return [new ActivationProblem(ActivationProblemKind.MarketNotEnabled)];
        }

        var problems = new List<ActivationProblem>();

        if (account.HedgeMode)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.FuturesHedgeMode));
        }

        if (account.MultiAssetsMode)
        {
            problems.Add(new ActivationProblem(ActivationProblemKind.FuturesMultiAssetsMode));
        }

        var required = RequiredMargin(symbol, caps.Apply(settings.Risk).MaxLeverage);

        // Without a minimum notional, any margin at all opens something.
        if (required > 0m ? account.AvailableMargin < required : account.AvailableMargin <= 0m)
        {
            problems.Add(new ActivationProblem(
                ActivationProblemKind.FuturesMarginInsufficient,
                Asset: symbol.QuoteAsset,
                Required: required,
                Available: account.AvailableMargin));
        }

        return problems;
    }

    /// <summary>
    /// The margin the smallest position Binance accepts for the pair needs: its minimum notional over the leverage,
    /// rounded up to Binance's 8 decimal places, so the amount the user reads is enough. Zero when Binance sets no
    /// minimum.
    /// </summary>
    public static decimal RequiredMargin(ExchangeSymbolInfo symbol, int leverage) =>
        symbol.Notional is { Minimum: > 0m } notional
            ? decimal.Round(notional.Minimum / Math.Max(leverage, 1), 8, MidpointRounding.ToPositiveInfinity)
            : 0m;

    /// <summary>
    /// Each required role needs a model that's Active in the catalog, that one of the user's valid AI keys can
    /// use, and that doesn't lack structured output (FR-ROLE-02). An optional role's model and every fallback
    /// are checked the same way, but only warn. So does a Strategist and Risk Critic on one provider
    /// (FR-ROLE-04), and a Sentiment Analyst that can't search.
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

        var user = await roleModels.ReadAsync(userId, keys, cancellationToken);

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

            if (user.ProblemOf(assignment.Model) is { } problem)
            {
                if (AgentRoles.IsRequired(role))
                {
                    problems.Add(new ActivationProblem(RequiredProblemOf(problem), role));
                }
                else
                {
                    warnings.Add(new ActivationWarning(OptionalWarningOf(problem), role));
                }
            }
            else if (role == AiRole.SentimentAnalyst && user.Models[assignment.Model].Capabilities.LiveSearch == false)
            {
                warnings.Add(new ActivationWarning(ActivationWarningKind.SentimentWithoutLiveSearch, role));
            }

            if (assignment.Fallback is { } fallback && user.ProblemOf(fallback) is { } fallbackProblem)
            {
                warnings.Add(new ActivationWarning(FallbackWarningOf(fallbackProblem), role));
            }
        }

        if (settings.RoleOf(AiRole.Strategist) is { } strategist
            && settings.RoleOf(AiRole.RiskCritic) is { } critic
            && strategist.Model.Provider == critic.Model.Provider)
        {
            warnings.Add(new ActivationWarning(ActivationWarningKind.RiskCriticSharesProvider, AiRole.RiskCritic));
        }
    }

    private static ActivationProblemKind RequiredProblemOf(ModelProblem problem) => problem switch
    {
        ModelProblem.NotAccessible => ActivationProblemKind.ModelNotAccessible,
        ModelProblem.NotActive => ActivationProblemKind.ModelNotActive,
        ModelProblem.LacksStructuredOutput => ActivationProblemKind.ModelLacksStructuredOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    private static ActivationWarningKind OptionalWarningOf(ModelProblem problem) =>
        problem == ModelProblem.LacksStructuredOutput ? ActivationWarningKind.OptionalModelLacksStructuredOutput : ActivationWarningKind.OptionalModelUnavailable;

    private static ActivationWarningKind FallbackWarningOf(ModelProblem problem) =>
        problem == ModelProblem.LacksStructuredOutput ? ActivationWarningKind.FallbackLacksStructuredOutput : ActivationWarningKind.FallbackUnavailable;

    private static class FuturesContract
    {
        /// <summary>As Binance names it. A delivery contract has no funding and expires.</summary>
        public const string Perpetual = "PERPETUAL";
    }
}
