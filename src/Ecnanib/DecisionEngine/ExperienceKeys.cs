using Ecnanib.Experience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The key from the scored calls of Experience to the cycle that each was made in:
/// <c>analyst_calls (cycle_id, user_id)</c> points at <c>decision_cycles (id, user_id)</c>.
/// See "The data" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is declared here, and not beside the entity.</b> A key between two components is
/// configured in the one that may reference the other, and Experience may not use the Decision
/// Engine. So the table has two configurations: its own, in Experience, for everything but this
/// key, and this one. See "The model" in docs/persistence.md.
/// </para>
/// <para>
/// <b>It is named for Experience, so that it is applied after the configuration of the cycle:</b>
/// configurations are applied in the order of their names, and the key of the cycles that this
/// one points at is named from the name that the table of the cycles has by then. See
/// <see cref="PositionFundingConfiguration"/>.
/// </para>
/// </remarks>
internal sealed class ExperienceAnalystCallConfiguration : IEntityTypeConfiguration<AnalystCall>
{
    public void Configure(EntityTypeBuilder<AnalystCall> builder)
    {
        // The calls of a cycle go with it, and so with its agent and with the account. The key
        // carries the owner along, so the call of a cycle is its user's (NFR-DAT-9).
        builder.HasOne<DecisionCycle>()
            .WithMany()
            .HasForeignKey(call => new { call.CycleId, call.UserId })
            .HasPrincipalKey(cycle => new { cycle.Id, cycle.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// The key from the shadow trades of Experience to the cycle whose plan each is of:
/// <c>shadow_trades (cycle_id, user_id)</c> points at <c>decision_cycles (id, user_id)</c>.
/// See "The data" in docs/experience.md.
/// </summary>
/// <remarks>It is declared here, and named as it is, for what <see cref="ExperienceAnalystCallConfiguration"/> says.</remarks>
internal sealed class ExperienceShadowTradeConfiguration : IEntityTypeConfiguration<ShadowTrade>
{
    public void Configure(EntityTypeBuilder<ShadowTrade> builder)
    {
        // The shadow trade of a cycle goes with it, and so with its agent and with the account.
        // The key carries the owner along (NFR-DAT-9).
        builder.HasOne<DecisionCycle>()
            .WithMany()
            .HasForeignKey(trade => new { trade.CycleId, trade.UserId })
            .HasPrincipalKey(cycle => new { cycle.Id, cycle.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
