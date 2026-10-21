namespace Ecnanib.Database.Entities;

/// <summary>UI theme preference, system by default (C-06, FR-UI-02).</summary>
public enum UiTheme
{
    System = 1,
    Dark = 2,
    Light = 3,
}

/// <summary>Validation state of a stored exchange or provider key (FR-KEY-02, FR-KEY-03, FR-KEY-07).</summary>
public enum KeyStatus
{
    Unvalidated = 1,
    Valid = 2,
    Invalid = 3,
}

/// <summary>Catalog status of a provider model (FR-SYNC-01, FR-SYNC-03).</summary>
public enum ModelStatus
{
    Available = 1,
    Deprecated = 2,
}

/// <summary>Result of the last catalog sync for a provider (FR-SYNC-06).</summary>
public enum SyncStatus
{
    Never = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>What started a catalog sync (FR-SYNC-02).</summary>
public enum SyncTrigger
{
    KeyValidation = 1,
    Scheduled = 2,
    Manual = 3,
}

/// <summary>Paper or live trading (FR-AGT-01, FR-AGT-11).</summary>
public enum AgentMode
{
    Paper = 1,
    Live = 2,
}

/// <summary>Agent lifecycle state (Appendix E).</summary>
public enum AgentState
{
    Draft = 1,
    Running = 2,
    Paused = 3,
    Stopped = 4,
    Error = 5,
}

/// <summary>Why a Paused agent is paused; <see cref="DailyLoss"/> resumes automatically (FR-EXE-09, FR-KEY-07).</summary>
public enum PauseCause
{
    User = 1,
    DailyLoss = 2,
    KeyInvalid = 3,
}

/// <summary>Council roles in execution order; the Risk Manager always runs last (Appendix B, FR-AI-02).</summary>
public enum CouncilRole
{
    MarketAnalyst = 1,
    SentimentFlow = 2,
    CrossMarketContext = 3,
    RiskManager = 4,
}

/// <summary>Recurring weekly or one-off "no new entry" window (FR-STR-05).</summary>
public enum EntryWindowKind
{
    Recurring = 1,
    OneOff = 2,
}

/// <summary>How an evaluation ended (FR-AGT-10, FR-AI-08); <see cref="InProgress"/> while it runs (<c>finished_at</c> is null).</summary>
public enum EvaluationOutcome
{
    NoAction = 1,
    Decided = 2,
    Failed = 3,
    Skipped = 4,
    InProgress = 5,
}

/// <summary>The Risk Manager's final action (FR-AI-10, Appendix C).</summary>
public enum DecisionAction
{
    OpenLong = 1,
    OpenShort = 2,
    Hold = 3,
    Modify = 4,
    Close = 5,
    NoTrade = 6,
}

/// <summary>Outcome of one role's turn in the council (FR-AI-07, FR-AI-12).</summary>
public enum RoleOutputStatus
{
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Disabled = 4,
    Skipped = 5,
}

/// <summary>Why an order exists in the trade lifecycle (FR-EXE-01..03, FR-EXE-06, FR-EXE-10).</summary>
public enum OrderPurpose
{
    Entry = 1,
    StopLoss = 2,
    TakeProfit = 3,
    Close = 4,
    /// <summary>A market exit of part of the position decided by the council or the strategy rules (FR-EXE-06).</summary>
    PartialClose = 5,
}

/// <summary>Direction of a position (FR-AGT-12).</summary>
public enum PositionSide
{
    Long = 1,
    Short = 2,
}

/// <summary>What closed a trade (FR-EXE-06, FR-EXE-08, FR-EXE-10, FR-EXP-04).</summary>
public enum TradeExitReason
{
    Stop = 1,
    Target = 2,
    Council = 3,
    KillSwitch = 4,
    External = 5,
    Manual = 6,
}

/// <summary>Hypothetical result of a setup the council rejected (FR-EXP-03).</summary>
public enum ShadowResult
{
    Pending = 1,
    HitTarget = 2,
    HitStop = 3,
    Expired = 4,
}

/// <summary>Scope of an experience statistic (FR-EXP-05).</summary>
public enum StatScope
{
    Agent = 1,
    User = 2,
    System = 3,
}
