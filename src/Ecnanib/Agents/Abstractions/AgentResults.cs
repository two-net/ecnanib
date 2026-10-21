using Ecnanib.Accounts.Abstractions;

namespace Ecnanib.Agents.Abstractions;

// User mistakes are results, never exceptions: the pages turn them into messages. Exceptions mean infrastructure failed.

/// <summary>One rejected input, named by its field so a form can show it in place.</summary>
public sealed record FieldError(string Field, string Message);

public enum AgentSaveStatus
{
    Succeeded,
    /// <summary>One or more fields were rejected; see <see cref="AgentSaveResult.Errors"/>. Nothing was saved.</summary>
    InvalidInput,
    /// <summary>The fair-use limit on agents per user is reached (FR-AGT-15); see <see cref="AgentSaveResult.Limit"/>.</summary>
    LimitReached,
    NotFound,
    /// <summary>A changed field is not editable in the agent's current state (FR-AGT-07); <see cref="AgentSaveResult.Detail"/> names it.</summary>
    InvalidState,
    /// <summary>The symbol could not be checked with the exchange; try again (FR-DATA-01).</summary>
    ExchangeUnavailable,
    /// <summary>Someone else changed the agent meanwhile; reload and try again.</summary>
    Conflict,
}

public sealed record AgentSaveResult(
    AgentSaveStatus Status,
    Guid? AgentId = null,
    IReadOnlyList<FieldError>? Errors = null,
    string? Detail = null,
    FairUseCheck? Limit = null)
{
    public static AgentSaveResult Invalid(params FieldError[] errors) => new(AgentSaveStatus.InvalidInput, Errors: errors);

    public static AgentSaveResult Invalid(IReadOnlyList<FieldError> errors) => new(AgentSaveStatus.InvalidInput, Errors: errors);
}

public enum AgentActionStatus
{
    Succeeded,
    NotFound,
    /// <summary>The action is not allowed in the current state (Appendix E); <see cref="AgentActionResult.Detail"/> says which.</summary>
    InvalidState,
    /// <summary>The fair-use limit on running agents per user is reached (FR-AGT-15).</summary>
    LimitReached,
    /// <summary>A live-mode start needs the risk disclaimer accepted first (FR-ACC-08).</summary>
    DisclaimerRequired,
    /// <summary>A live-mode start needs a Valid exchange key allowed for the market (FR-KEY-08, FR-KEY-09).</summary>
    KeyUnavailable,
    /// <summary>An enabled council role's provider has no Valid key (FR-KEY-08); <see cref="AgentActionResult.Detail"/> lists the providers.</summary>
    ProviderKeyMissing,
    /// <summary>The agent's strategy, at its pinned version, is no longer in the catalog; upgrade it or choose another (FR-STR-06).</summary>
    StrategyUnavailable,
    /// <summary>The agent's configuration cannot run as it is (a council role without a model, leverage over the cap); <see cref="AgentActionResult.Detail"/> says what.</summary>
    InvalidConfiguration,
    /// <summary>The action could not be completed (an order could not be cancelled); the state is unchanged.</summary>
    Failed,
}

/// <summary><see cref="AgentId"/> is the new agent for a successful Clone.</summary>
public sealed record AgentActionResult(AgentActionStatus Status, string? Detail = null, FairUseCheck? Limit = null, Guid? AgentId = null);

public sealed record AgentBulkFailure(Guid AgentId, string Name, AgentActionResult Result);

/// <summary>Outcome of "stop all my agents" (FR-EXE-10): how many stopped and which could not be, each with its own result.</summary>
public sealed record AgentBulkActionResult(int Stopped, IReadOnlyList<AgentBulkFailure> Failed);
