using Ecnanib.Accounts;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Agents;

/// <summary>
/// The model that one role of an agent's team was given, as a row of the <c>agent_roles</c>
/// table (FR-AGT-5). An agent has one row for each role that has a model, and four when its team
/// is whole (FR-TEAM-1). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// The row names the model, and not its provider: a model is of one provider, so the provider of
/// a role is the one of its model. The choice is of the agent, so two agents of one user can
/// have different models of the same provider.
/// </para>
/// <para>
/// It is a setting of the agent, as its risk settings are, and no result of what the agent did:
/// the row says no mode, and stays as it is when the agent is switched between Demo and Live.
/// </para>
/// </remarks>
/// <param name="agentId">The agent whose role it is.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="role">The role.</param>
/// <param name="modelId">The model of the catalog that the role was given.</param>
public sealed class AgentRole(Guid agentId, Guid userId, Role role, Guid modelId) : IUserOwned
{
    public Guid AgentId { get; } = agentId;

    public Guid UserId { get; } = userId;

    public Role Role { get; } = role;

    public Guid ModelId { get; } = modelId;
}

internal sealed class AgentRoleConfiguration : IEntityTypeConfiguration<AgentRole>
{
    public void Configure(EntityTypeBuilder<AgentRole> builder)
    {
        builder.ToTable("agent_roles");

        // An agent has each role once, so a role of an agent is found by the two.
        builder.HasKey(row => new { row.AgentId, row.Role });

        builder.Property(row => row.AgentId);
        builder.Property(row => row.UserId);
        builder.Property(row => row.Role);
        builder.Property(row => row.ModelId);

        // The roles of an agent go with it when it is deleted.
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(row => row.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        // They belong to the account, and go with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(row => row.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A role is a seeded row, which is never deleted while a row points at it.
        builder.HasOne<RoleRow>()
            .WithMany()
            .HasForeignKey(row => row.Role)
            .OnDelete(DeleteBehavior.Restrict);

        // A model of the catalog is never deleted (FR-CAT-5): one that its provider no longer
        // offers stays, and the role still points at it, until its user gives the role another.
        // The index of the key finds the agents that use a model, when the model goes.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(row => row.ModelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
