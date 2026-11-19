using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The model that held one role of an agent when a position of the agent was opened, as a row of
/// the <c>position_roles</c> table (FR-EXP-1). See "The results of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is written with the position, and never changed.</b> The roles of an agent are settings,
/// which its user changes from one cycle to the next: what the agent has now says nothing of who
/// decided a position that is long closed. The record of a cycle says the model of each call,
/// and is kept for a year (NFR-DAT-5), where a position is kept for good. So a position says it
/// itself.
/// </para>
/// <para>
/// A role that had no model when the position was opened has no row. The row names the model,
/// and not its provider: a model is of one provider, and is never deleted (FR-CAT-5).
/// </para>
/// <para>
/// It carries its owner along to the position that it hangs on: <c>(position_id, user_id)</c>
/// points at <c>positions (id, user_id)</c>. The position says the mode that the agent was in
/// (FR-BIN-11). See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="positionId">The position.</param>
/// <param name="userId">The user whose agent the position is of.</param>
/// <param name="role">The role.</param>
/// <param name="modelId">The model of the catalog that held the role, which says its provider.</param>
public sealed class PositionRole(Guid positionId, Guid userId, Role role, Guid modelId) : IUserOwned
{
    public Guid PositionId { get; } = positionId;

    public Guid UserId { get; } = userId;

    public Role Role { get; } = role;

    public Guid ModelId { get; } = modelId;
}

/// <remarks>It is named for the position, so that it is applied after the configuration of the position: see <see cref="PositionFundingConfiguration"/>.</remarks>
internal sealed class PositionRoleConfiguration : IEntityTypeConfiguration<PositionRole>
{
    public void Configure(EntityTypeBuilder<PositionRole> builder)
    {
        builder.ToTable("position_roles");

        // A position has each role once.
        builder.HasKey(row => new { row.PositionId, row.Role });

        builder.Property(row => row.PositionId);
        builder.Property(row => row.UserId);
        builder.Property(row => row.Role);
        builder.Property(row => row.ModelId);

        // The roles of a position go with it, which goes with its agent, and with the account
        // when that is deleted. The key carries the owner along (NFR-DAT-9).
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(row => new { row.PositionId, row.UserId })
            .HasPrincipalKey(position => new { position.Id, position.UserId })
            .OnDelete(DeleteBehavior.Cascade);

        // A role is a seeded row, which is never deleted while a row points at it.
        builder.HasOne<RoleRow>()
            .WithMany()
            .HasForeignKey(row => row.Role)
            .OnDelete(DeleteBehavior.Restrict);

        // A model of the catalog is never deleted (FR-CAT-5), so the row finds the model that
        // held the role, and its provider, for good. The index of the key finds the positions
        // that a model decided.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(row => row.ModelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
