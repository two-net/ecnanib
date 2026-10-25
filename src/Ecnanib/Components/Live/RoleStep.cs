using Ecnanib.Database.Entities;

namespace Ecnanib.Components.Live;

/// <summary>Where a council role is in a deliberation shown as a sequence (FR-UI-03 b).</summary>
public enum RoleStage
{
    Waiting = 1,
    Thinking = 2,
    Done = 3,
}

/// <summary>One role of the agent's council, in sequence order; a disabled role is shown greyed and never thinks.</summary>
public sealed record RoleSlot(CouncilRole Role, int Sequence, bool Enabled);

/// <summary>A role's progress: thinking while its model is called, done with the recorded status and, from a replay, what it proposed.</summary>
public sealed record RoleStep(RoleStage Stage, RoleOutputStatus? Status, DecisionAction? Proposed);
