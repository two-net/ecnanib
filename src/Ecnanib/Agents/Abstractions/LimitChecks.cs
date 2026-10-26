namespace Ecnanib.Agents.Abstractions;

/// <summary>Outcome of one hard limit (FR-AI-11): the output passed it, was clamped to it, or violated it.</summary>
public enum LimitCheckStatus
{
    Pass = 1,
    Clamped = 2,
    Fail = 3,
}

/// <summary>One entry of a decision's limits check (Appendix C); stored as <c>decisions.limits_check</c> by the council and <c>decisions.execution_check</c> by execution.</summary>
public sealed record LimitCheck(string Limit, LimitCheckStatus Status, string Detail);

/// <summary>The limits the council enforces after the Risk Manager (FR-EXE-04, FR-EXE-09, FR-AI-15).</summary>
public static class LimitNames
{
    public const string EntriesAllowed = "entriesAllowed";
    public const string NoEntryWindow = "noEntryWindow";
    public const string SetupRequired = "setupRequired";
    public const string Direction = "direction";
    public const string DailyLoss = "dailyLoss";
    public const string OpenPositions = "openPositions";
    public const string StopRequired = "stopRequired";
    public const string StopSide = "stopSide";
    public const string StopDistance = "stopDistance";
    public const string RiskPerTrade = "riskPerTrade";
    public const string Leverage = "leverage";
    public const string Targets = "targets";
    public const string PositionRequired = "positionRequired";
    public const string StopTightenOnly = "stopTightenOnly";
    public const string PartialFraction = "partialFraction";
}

/// <summary>The checks execution appends at order time (FR-EXE-04): sizing against the filters, the notional cap, the balance, the daily loss and, after a restart, whether the exchange state could be verified (NFR-REL-01).</summary>
public static class ExecutionLimitNames
{
    public const string ExchangeState = "exchangeState";
    public const string Size = "size";
    public const string Notional = "notional";
    public const string MaxQuantity = "maxQuantity";
    public const string MinQuantity = "minQuantity";
    public const string MinNotional = "minNotional";
    public const string Balance = "balance";
    public const string DailyLoss = "dailyLoss";
    public const string AlgoHeadroom = "algoHeadroom";
    public const string MarketFallback = "marketFallback";
    public const string PartialFraction = "partialFraction";
}
