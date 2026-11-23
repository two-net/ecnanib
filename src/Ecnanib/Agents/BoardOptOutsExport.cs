using Ecnanib.Accounts;
using Ecnanib.Experience;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// That a user left the platform-wide board, as the export of the user's data holds it
/// (NFR-SEC-9, FR-EXP-10).
/// </summary>
/// <param name="OptedOutAt">When the user left the board.</param>
public sealed record ExportedBoardOptOut(DateTimeOffset OptedOutAt);

/// <summary>
/// Whether a user left the platform-wide board, for the export of their data (NFR-SEC-9,
/// FR-EXP-10): a preference of the user, which is in the export.
/// </summary>
/// <remarks>
/// Experience keeps the choice, and may not use Accounts, which declares what an export is made
/// of. So Agents hands it over, which may use both, as it hands the lessons over: see
/// <see cref="LessonsExport"/>. The section has one row for a user who left the board, and none
/// for a user who contributes. See "Exporting your data" in docs/accounts.md.
/// </remarks>
internal sealed class BoardOptOutsExport : IExportedPart
{
    public string Section => "boardOptOuts";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // The row of the user, where there is one: asked for by owner.
        var optOuts = await context.Set<BoardOptOut>()
            .AsNoTracking()
            .OwnedBy(userId)
            .ToListAsync(cancellationToken);

        return [.. optOuts.Select(optOut => new ExportedBoardOptOut(optOut.OptedOutAt))];
    }
}
