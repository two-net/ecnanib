using System.Text.RegularExpressions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Services;

/// <summary>The settings of an agent after normalisation: trimmed name, upper-case symbol, mode-dependent fields cleared.</summary>
internal sealed record AgentSettings(
    string Name,
    MarketType MarketType,
    string Symbol,
    CandleInterval Timeframe,
    AgentMode Mode,
    Guid? ExchangeKeyId,
    int? Leverage,
    MarginType? MarginType,
    bool Discretionary);

/// <summary>
/// Input rules that need no database (FR-AGT-01/02/12/13/14, FR-AI-03/05, Appendix A). They run before anything is
/// looked up, so a bad form never costs a query, and the messages are what the pages show.
/// </summary>
internal static partial class AgentRules
{
    public const int MaxNameLength = 100;

    public const int MaxSymbolLength = 30;

    public const int MaxModelLength = 200;

    public static readonly IReadOnlyList<CouncilRole> Roles =
    [
        CouncilRole.MarketAnalyst, CouncilRole.SentimentFlow, CouncilRole.CrossMarketContext, CouncilRole.RiskManager,
    ];

    public static bool IsAgentTimeframe(CandleInterval timeframe) => AgentsOptions.Timeframes.Contains(timeframe);

    public static AgentSettings? NormalizeSettings(
        string? name, MarketType marketType, string? symbol, CandleInterval timeframe, AgentMode mode, Guid? exchangeKeyId,
        int? leverage, MarginType? marginType, bool discretionary, RiskCapsOptions caps, List<FieldError> errors)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
            errors.Add(new FieldError("name", $"Enter a name of at most {MaxNameLength} characters."));

        if (!Enum.IsDefined(marketType))
            errors.Add(new FieldError("marketType", "Choose Spot or Futures."));

        symbol = symbol?.Trim().ToUpperInvariant() ?? "";
        if (symbol.Length is 0 or > MaxSymbolLength || !SymbolPattern().IsMatch(symbol))
            errors.Add(new FieldError("symbol", "Enter a symbol such as BTCUSDT."));

        if (!IsAgentTimeframe(timeframe))
            errors.Add(new FieldError("timeframe", "Choose one of 15m, 1H, 4H or 1D."));

        if (!Enum.IsDefined(mode))
            errors.Add(new FieldError("mode", "Choose paper or live."));

        if (mode == AgentMode.Live && exchangeKeyId is null)
            errors.Add(new FieldError("exchangeKeyId", "Choose a Binance key for live mode."));
        if (mode == AgentMode.Paper)
            exchangeKeyId = null;

        if (marketType == MarketType.UsdFutures)
        {
            if (leverage is null || leverage < 1 || leverage > caps.MaxLeverage)
                errors.Add(new FieldError("leverage", $"Leverage must be between 1 and {caps.MaxLeverage}."));
            marginType ??= MarginType.Isolated;
            if (!Enum.IsDefined(marginType.Value))
                errors.Add(new FieldError("marginType", "Choose isolated or cross margin."));
        }
        else
        {
            leverage = null;
            marginType = null;
        }

        return errors.Count == 0 ? new AgentSettings(name, marketType, symbol, timeframe, mode, exchangeKeyId, leverage, marginType, discretionary) : null;
    }

    public static List<FieldError> ValidateCouncil(CouncilInput input)
    {
        var errors = new List<FieldError>();
        if (input.Roles.Count != Roles.Count || Roles.Any(role => input.Roles.Count(r => r.Role == role) != 1))
        {
            errors.Add(new FieldError("council", "Every council role must appear exactly once."));
            return errors;
        }

        foreach (var role in input.Roles)
        {
            var field = $"council.{CamelCase(role.Role)}";
            if (!Enum.IsDefined(role.Provider))
                errors.Add(new FieldError(field + ".provider", $"Choose a provider for the {Display(role.Role)}."));
            if (role.Role == CouncilRole.RiskManager && !role.Enabled)
                errors.Add(new FieldError(field + ".enabled", "The Risk Manager cannot be disabled."));
            var model = role.Model?.Trim() ?? "";
            if (model.Length > MaxModelLength)
                errors.Add(new FieldError(field + ".model", $"The model id must be at most {MaxModelLength} characters."));
            else if (role.Enabled && model.Length == 0)
                errors.Add(new FieldError(field + ".model", $"Choose a model for the {Display(role.Role)}."));
        }

        return errors;
    }

    public static List<FieldError> ValidateRiskLimits(RiskLimitsInput input, RiskCapsOptions caps)
    {
        var errors = new List<FieldError>();
        if (input.RiskPerTradePercent <= 0 || input.RiskPerTradePercent > caps.MaxRiskPerTradePercent)
            errors.Add(new FieldError("riskLimits.riskPerTradePercent", $"Risk per trade must be above 0 and at most {caps.MaxRiskPerTradePercent}%."));
        if (input.MaxOpenPositions < 1 || input.MaxOpenPositions > caps.MaxOpenPositions)
            errors.Add(new FieldError("riskLimits.maxOpenPositions", $"Open positions must be between 1 and {caps.MaxOpenPositions}."));
        if (input.DailyLossLimitPercent <= 0 || input.DailyLossLimitPercent > caps.MaxDailyLossPercent)
            errors.Add(new FieldError("riskLimits.dailyLossLimitPercent", $"The daily loss limit must be above 0 and at most {caps.MaxDailyLossPercent}%."));
        if (input.MaxNotional is <= 0)
            errors.Add(new FieldError("riskLimits.maxNotional", "The notional cap must be above 0, or empty for none."));
        return errors;
    }

    /// <summary>The Appendix B default provider of a role.</summary>
    public static Ecnanib.Ai.Abstractions.AiProvider DefaultProvider(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => Ecnanib.Ai.Abstractions.AiProvider.Gpt,
        CouncilRole.SentimentFlow => Ecnanib.Ai.Abstractions.AiProvider.Grok,
        CouncilRole.CrossMarketContext => Ecnanib.Ai.Abstractions.AiProvider.Gemini,
        CouncilRole.RiskManager => Ecnanib.Ai.Abstractions.AiProvider.Claude,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown council role."),
    };

    public static string Display(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => "Market Analyst",
        CouncilRole.SentimentFlow => "Sentiment & Flow",
        CouncilRole.CrossMarketContext => "Cross-Market Context",
        CouncilRole.RiskManager => "Risk Manager",
        _ => role.ToString(),
    };

    public static string CamelCase(CouncilRole role)
    {
        var text = role.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    [GeneratedRegex("^[A-Z0-9]+$")]
    private static partial Regex SymbolPattern();
}
