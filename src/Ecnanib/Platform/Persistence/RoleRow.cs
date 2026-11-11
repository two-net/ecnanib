using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// A <see cref="Role"/> as a row of the <c>roles</c> table, so that a table which records a role
/// can point at it. The rows are the members of the enum, seeded through migrations (NFR-DAT-10).
/// </summary>
/// <remarks>
/// It lives in the platform because the role is shared vocabulary: every component may point at
/// it, and no component owns it.
/// </remarks>
public sealed class RoleRow(Role code, string displayName)
{
    public Role Code { get; } = code;

    /// <summary>The name shown to users, such as "Market Analyst".</summary>
    public string DisplayName { get; } = displayName;
}

internal sealed class RoleRowConfiguration : IEntityTypeConfiguration<RoleRow>
{
    public void Configure(EntityTypeBuilder<RoleRow> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(role => role.Code);
        builder.Property(role => role.DisplayName);

        // One row per member, so the table cannot fall behind the enum: a new member changes
        // the model, and the model must match its migrations.
        builder.HasData(Enum.GetValues<Role>().Select(role => new RoleRow(role, DisplayNameOf(role))));
    }

    private static string DisplayNameOf(Role role) => role switch
    {
        Role.MarketAnalyst => "Market Analyst",
        Role.SentimentAnalyst => "Sentiment Analyst",
        Role.Strategist => "Strategist",
        Role.RiskManager => "Risk Manager",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no display name."),
    };
}
