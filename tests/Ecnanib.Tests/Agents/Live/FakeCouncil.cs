using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>
/// A council for the live tests: it answers at once with an OpenLong limit decision, or waits at a gate or for its
/// cancellation, and records every call in order so a test can assert the scheduler's cap and ordering.
/// </summary>
internal sealed class FakeCouncil : ICouncil
{
    private readonly Lock _lock = new();
    private readonly List<Guid> _calls = [];

    /// <summary>The context of the last deliberation.</summary>
    public CouncilContext? Context { get; private set; }

    /// <summary>Agent ids in the order the council was asked.</summary>
    public IReadOnlyList<Guid> Calls
    {
        get
        {
            lock (_lock)
                return _calls.ToArray();
        }
    }

    /// <summary>When set, every deliberation waits here, under the caller's token, before answering.</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>When true, a deliberation never answers: it waits for its cancellation (the deadline or a lifecycle action).</summary>
    public bool WaitForCancellation { get; init; }

    /// <summary>Runs after the gate and before the answer; a test moves the fake clock here. Settable because the clock exists only once the host is built.</summary>
    public Action<CouncilContext>? BeforeDecision { get; set; }

    /// <summary>The decision to answer with; an OpenLong limit entry with one Risk Manager turn by default.</summary>
    public Func<CouncilContext, Decision> Decide { get; set; } = _ => OpenLong();

    public async Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken)
    {
        Context = context;
        lock (_lock)
            _calls.Add(context.Agent.Id);
        if (WaitForCancellation)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (Gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        BeforeDecision?.Invoke(context);
        return CouncilResult.Decided(Decide(context));
    }

    public static Decision OpenLong() => new()
    {
        FinalAction = DecisionAction.OpenLong, EntryType = OrderType.Limit, EntryPrice = 100m, StopPrice = 90m, Size = 0.1m, Rationale = "fake council",
        RoleOutputs = [RiskManager(DecisionAction.OpenLong)],
    };

    public static Decision Close(Guid tradeId) => new()
    {
        FinalAction = DecisionAction.Close, TradeId = tradeId, Rationale = "fake council: close",
        RoleOutputs = [RiskManager(DecisionAction.Close)],
    };

    private static DecisionRoleOutput RiskManager(DecisionAction action) =>
        new() { Role = CouncilRole.RiskManager, Sequence = 4, Provider = AiProvider.Claude, Model = "m", Status = RoleOutputStatus.Succeeded, ProposedAction = action };
}
