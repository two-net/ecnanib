using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Experience;

/// <summary>
/// That a user left the platform-wide board: none of their trades is counted there (FR-EXP-10).
/// See "The platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row says that its user is left out,</b> and a user with no row contributes: every user
/// does until they choose otherwise, and a user who comes back has no row again. So what reads
/// the trades of the board leaves out the users that have a row, and asks nothing else of them.
/// </para>
/// <para>
/// <b>It is the user's own choice, and a preference of theirs.</b> It says nothing of who the
/// user is: an id and a time. It is in the export of the user's data, which Agents hands over.
/// </para>
/// <para>
/// It points at the account: <c>board_opt_outs (user_id)</c> at <c>users (id)</c>, which Agents
/// declares, because Experience may not use Accounts. So the row goes with the account. See
/// docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="userId">The user who left the board.</param>
/// <param name="optedOutAt">When they left it.</param>
public sealed class BoardOptOut(Guid userId, DateTimeOffset optedOutAt) : IUserOwned
{
    public Guid UserId { get; } = userId;

    public DateTimeOffset OptedOutAt { get; } = optedOutAt;
}

internal sealed class BoardOptOutConfiguration : IEntityTypeConfiguration<BoardOptOut>
{
    public void Configure(EntityTypeBuilder<BoardOptOut> builder)
    {
        builder.ToTable("board_opt_outs");

        // A user has left the board or has not: the row is found by its user, and there is one
        // at the most.
        builder.HasKey(optOut => optOut.UserId);

        builder.Property(optOut => optOut.UserId).ValueGeneratedNever();
        builder.Property(optOut => optOut.OptedOutAt);

        // The key to the account, which deletes the row with it, is declared by Agents, in
        // BoardKeys.cs: Experience may not name an account.
    }
}
