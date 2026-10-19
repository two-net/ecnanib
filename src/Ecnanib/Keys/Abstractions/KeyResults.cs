using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Keys.Abstractions;

// User mistakes and refused keys are results, never exceptions: the pages turn them into messages. Exceptions mean
// infrastructure failed. A refused or unverifiable key is never stored: every stored key is Valid or Invalid.

public enum KeySaveStatus
{
    Succeeded,
    /// <summary>Blank or malformed label, key or secret; nothing was sent anywhere.</summary>
    InvalidInput,
    NoMarketSelected,
    LabelTaken,
    /// <summary>Binance reports withdrawal permission on the key (FR-KEY-02); it was not stored.</summary>
    WithdrawalEnabled,
    /// <summary>The service refused the key: invalid, revoked, wrong permissions or IP restriction.</summary>
    Rejected,
    /// <summary>Validation could not complete (outage, rate limit, timeout, unexpected answer); the key was not stored.</summary>
    Failed,
    NotFound,
}

/// <summary><see cref="Detail"/> is the exchange's or provider's own explanation (never containing more than the masked key), shown to the user.</summary>
public sealed record KeySaveResult(KeySaveStatus Status, Guid? KeyId = null, string? Detail = null, int ModelCount = 0, bool CatalogSynced = false);

public enum KeyValidationOutcome
{
    Valid,
    Invalid,
    /// <summary>Could not be checked; the stored status is unchanged.</summary>
    Failed,
    NotFound,
}

/// <summary>Result of "validate now" or a scheduled re-validation (FR-KEY-07). <see cref="AgentsFailed"/> counts agents moved to Error.</summary>
public sealed record KeyValidationResult(KeyValidationOutcome Outcome, string? Detail = null, int AgentsFailed = 0, int ModelCount = 0, bool CatalogSynced = false);

public enum KeyDeletionStatus
{
    Succeeded,
    NotFound,
    /// <summary>Running or Paused agents use the key and the caller did not ask to stop them (FR-KEY-06); nothing changed.</summary>
    AgentsInUse,
}

/// <summary><see cref="Agents"/> is the blocking count for <see cref="KeyDeletionStatus.AgentsInUse"/> and the stopped count for <see cref="KeyDeletionStatus.Succeeded"/>.</summary>
public sealed record KeyDeletionResult(KeyDeletionStatus Status, int Agents = 0);

/// <summary>A new Binance key pair as entered by the user (FR-KEY-01). Printing it shows the masked key only.</summary>
public sealed record ExchangeKeyInput(string? Label, ExchangeEnvironment Environment, bool AllowSpot, bool AllowFutures, string? ApiKey, string? ApiSecret)
{
    public override string ToString() =>
        $"ExchangeKeyInput({Environment}, spot={AllowSpot}, futures={AllowFutures}, {ExchangeCredentials.Mask(ApiKey ?? string.Empty)})";
}

public enum KeyResolutionStatus
{
    Resolved,
    /// <summary>The user has no such key (FR-KEY-08); also the answer for a key that belongs to someone else (NFR-SEC-03).</summary>
    Missing,
    /// <summary>The key is stored but its last validation failed; <see cref="KeyResolution{THandle}.Reason"/> says why.</summary>
    Invalid,
    /// <summary>The key exists but was not enabled for the requested market (FR-KEY-01).</summary>
    MarketNotAllowed,
}

/// <summary>A handle with credentials, or why none can be given. Handles are the only objects that carry key values out of the module.</summary>
public sealed record KeyResolution<THandle>(KeyResolutionStatus Status, THandle? Handle = null, string? Reason = null)
    where THandle : class
{
    public bool IsResolved => Status == KeyResolutionStatus.Resolved;
}
