using Ecnanib.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Agents;

/// <summary>
/// A switch of the service that is set while it runs, as a row of the <c>runtime_flags</c>
/// table: its name, whether it is on, who changed it last, and when (NFR-CFG-6). The one there
/// is, is the global halt (FR-OPS-3). A flag that was never switched has no row, and is off.
/// See "The global halt" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row belongs to the service, and to no user.</b> It says the same to every user, and
/// carries no owner: see docs/ownership.md. Who changed it last is kept as long as that account
/// is, and is set to nothing when the account is deleted, so that the flag stays as it is and
/// nothing of a deleted account is left. The audit trail says who switched it, and when
/// (FR-OPS-5).
/// </para>
/// <para>
/// This is state, and no history: one row for each flag, overwritten by each switch.
/// </para>
/// </remarks>
internal sealed class RuntimeFlag(string name, bool value, Guid? changedBy, DateTimeOffset changedAt)
{
    /// <summary>The name of the global halt, as its row has it.</summary>
    public const string GlobalHalt = "global-halt";

    /// <summary>The flag.</summary>
    public string Name { get; } = name;

    /// <summary>Whether it is on.</summary>
    public bool Value { get; } = value;

    /// <summary>The user who switched it last, or null when that account was deleted since.</summary>
    public Guid? ChangedBy { get; } = changedBy;

    /// <summary>When it was switched last.</summary>
    public DateTimeOffset ChangedAt { get; } = changedAt;
}

internal sealed class RuntimeFlagConfiguration : IEntityTypeConfiguration<RuntimeFlag>
{
    public void Configure(EntityTypeBuilder<RuntimeFlag> builder)
    {
        builder.ToTable("runtime_flags");
        builder.HasKey(flag => flag.Name);
        builder.Property(flag => flag.Value);
        builder.Property(flag => flag.ChangedBy);
        builder.Property(flag => flag.ChangedAt);

        // The flag stays when the account of whoever switched it is deleted: a halt that is on
        // is on for every user, and does not end with one account. The row keeps nothing of
        // that account then (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(flag => flag.ChangedBy)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
