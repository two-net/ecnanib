using Ecnanib.Agents;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// One cycle of an agent, as a row of the <c>decision_cycles</c> table: which agent, for which
/// candle close, when it started and ended, and how it ended. The calls of the roles of the
/// cycle hang on it: see <see cref="RoleOutput"/>. See "The record of a cycle" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// A cycle is recorded once, when it ends, however it ends: with a decision of the team, with
/// none, with an error, or stopped. A cycle that never started leaves no row.
/// </para>
/// <para>
/// It says the mode that the agent was in, and keeps saying it when the agent is switched: what
/// an agent decided in Demo is Demo for good (FR-BIN-11).
/// </para>
/// <para>
/// The Decision Engine may not use Accounts, so the row does not point at <c>users</c> itself.
/// It carries its owner along to the agent that it hangs on: <c>(agent_id, user_id)</c> points at
/// <c>agents (id, user_id)</c>, and goes with the agent, and so with the account. See
/// docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">The id of the cycle, which what started it assigned, and which everything logged for it carries.</param>
/// <param name="agentId">The agent whose cycle it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="mode">The mode that the agent was in.</param>
/// <param name="closedAt">The close of the candle that the cycle was for.</param>
/// <param name="startedAt">When the cycle started: when its turn came.</param>
/// <param name="endedAt">When the cycle ended.</param>
/// <param name="outcome">How it ended.</param>
public sealed class DecisionCycle(
    Guid id,
    Guid agentId,
    Guid userId,
    TradingMode mode,
    DateTimeOffset closedAt,
    DateTimeOffset startedAt,
    DateTimeOffset endedAt,
    CycleOutcome outcome) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public TradingMode Mode { get; } = mode;

    public DateTimeOffset ClosedAt { get; } = closedAt;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public DateTimeOffset EndedAt { get; } = endedAt;

    public CycleOutcome Outcome { get; } = outcome;
}

internal sealed class DecisionCycleConfiguration : IEntityTypeConfiguration<DecisionCycle>
{
    public void Configure(EntityTypeBuilder<DecisionCycle> builder)
    {
        builder.ToTable("decision_cycles");
        builder.HasKey(cycle => cycle.Id);

        // What starts a cycle assigns the id, so a row can be written without asking the database for one.
        builder.Property(cycle => cycle.Id).ValueGeneratedNever();
        builder.Property(cycle => cycle.AgentId);
        builder.Property(cycle => cycle.UserId);
        builder.Property(cycle => cycle.Mode);
        builder.Property(cycle => cycle.ClosedAt);
        builder.Property(cycle => cycle.StartedAt);
        builder.Property(cycle => cycle.EndedAt);
        builder.Property(cycle => cycle.Outcome);

        // The cycles of one agent of a user, in the order of their closes. It is also the index
        // of the key below.
        builder.HasIndex(cycle => new { cycle.AgentId, cycle.UserId, cycle.ClosedAt });

        // The cycles of an agent go with it when it is deleted, and with the account when that
        // is. The key carries the owner along, so the cycle of an agent is its user's (NFR-DAT-9).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(cycle => new { cycle.AgentId, cycle.UserId })
            .HasPrincipalKey(agent => new { agent.Id, agent.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
