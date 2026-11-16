using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Agents;

/// <summary>
/// One change of the state of an agent, as a row of the <c>agent_state_changes</c> table: the
/// state the agent came to, why, and when (FR-AGT-10). The first row of an agent is its
/// creation. See docs/agents.md.
/// </summary>
/// <param name="id">Assigned by the application when the change is recorded. A later change has a greater id.</param>
/// <param name="agentId">The agent whose state changed.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="state">The state the agent came to.</param>
/// <param name="cause">Why it did.</param>
/// <param name="changedAt">When it did.</param>
public sealed class AgentStateChange(
    Guid id, Guid agentId, Guid userId, AgentState state, AgentStateCause cause, DateTimeOffset changedAt) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public AgentState State { get; } = state;

    public AgentStateCause Cause { get; } = cause;

    public DateTimeOffset ChangedAt { get; } = changedAt;
}

internal sealed class AgentStateChangeConfiguration : IEntityTypeConfiguration<AgentStateChange>
{
    public void Configure(EntityTypeBuilder<AgentStateChange> builder)
    {
        builder.ToTable("agent_state_changes");
        builder.HasKey(change => change.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(change => change.Id).ValueGeneratedNever();
        builder.Property(change => change.AgentId);
        builder.Property(change => change.UserId);
        builder.Property(change => change.State);
        builder.Property(change => change.Cause);
        builder.Property(change => change.ChangedAt);

        // The changes of one agent, in the order they happened in.
        builder.HasIndex(change => new { change.AgentId, change.ChangedAt });

        // The changes of an agent go with it when it is deleted.
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(change => change.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        // They belong to the account, and go with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(change => change.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Changes the state of an agent, and records the change: the one way the state of an agent is
/// written, for what a user does to it and for what the service does (FR-AGT-10). So the row of
/// an agent and the latest of its changes say the same.
/// </summary>
internal static class AgentStateChanges
{
    // The millisecond of the id that was handed out last.
    private static long _lastMillisecond;

    /// <summary>The first change of an agent: its creation, in the state and at the time that its row says.</summary>
    /// <param name="agent">The agent, which is stored with the change.</param>
    public static AgentStateChange First(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentStateChange(NextId(), agent.Id, agent.UserId, agent.State, agent.StateCause, agent.StateChangedAt);
    }

    /// <summary>
    /// Moves an agent of a user to a state, inside the transaction of whoever asks: the row of
    /// the agent says the state, its cause, and since when, and a change with the same three is
    /// recorded. Whoever asks has locked the row of the account, and commits.
    /// </summary>
    /// <remarks>
    /// It saves the context: the statement that updates the row directly does not write what the
    /// context has been handed, so the change is written now and not with a later save.
    /// </remarks>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="state">The state it comes to.</param>
    /// <param name="cause">Why it does.</param>
    /// <param name="at">When it does.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The context holds no transaction. A state that is written apart from its change, or from
    /// what caused it, could stand when the other does not.
    /// </exception>
    public static async Task MoveAsync(
        EcnanibDbContext context,
        Guid userId,
        Guid agentId,
        AgentState state,
        AgentStateCause cause,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The state of an agent is changed inside the transaction of what changes it, and this context holds none.");
        }

        await context.Set<Agent>()
            .OwnedBy(userId)
            .Where(agent => agent.Id == agentId)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(agent => agent.State, state)
                    .SetProperty(agent => agent.StateCause, cause)
                    .SetProperty(agent => agent.StateChangedAt, at),
                cancellationToken);

        context.Set<AgentStateChange>().Add(new AgentStateChange(NextId(), agentId, userId, state, cause, at));

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The id of the next change. An id starts with a time in milliseconds, and each one that is
    /// handed out is of a later millisecond than the one before. So the ids of the changes that
    /// this process records are in the order the changes were recorded in, also for two changes
    /// that the clock gives the same time.
    /// </summary>
    private static Guid NextId()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long last;
        long next;
        do
        {
            last = Volatile.Read(ref _lastMillisecond);
            next = Math.Max(now, last + 1);
        }
        while (Interlocked.CompareExchange(ref _lastMillisecond, next, last) != last);

        return Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(next));
    }
}
