using Ecnanib.Accounts;
using Ecnanib.Experience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Agents;

/// <summary>
/// The key from who left the platform-wide board of Experience to the account:
/// <c>board_opt_outs (user_id)</c> points at <c>users (id)</c> (FR-EXP-10). See "The data" in
/// docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is declared here, and beside neither entity.</b> A key between two components is
/// configured in one that may reference both. Experience may not use Accounts, so that what
/// recalls lessons into prompts stays out of reach of personal data (FR-PRM-9), and Accounts
/// uses no other component. This component may use both, as it does to hand the lessons over to
/// the export of a user's data: see <see cref="LessonsExport"/>. So the table has its own
/// configuration, in Experience, for everything but this key. See "The model" in
/// docs/persistence.md.
/// </para>
/// <para>
/// It is applied after the configuration of the account, which names the table that it points
/// at: configurations are applied in the order of their names, and Accounts comes before Agents.
/// </para>
/// </remarks>
internal sealed class UserBoardOptOutConfiguration : IEntityTypeConfiguration<BoardOptOut>
{
    public void Configure(EntityTypeBuilder<BoardOptOut> builder)
    {
        // The choice belongs to the account and goes with it when it is deleted (FR-ACC-9). It
        // is the user's own, so the row says whose it is (NFR-DAT-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(optOut => optOut.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
