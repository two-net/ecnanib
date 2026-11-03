using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Identity;

/// <summary>Leaves out the actor's ID and the correlation ID, which are for administrators (FR-ADM-06).</summary>
internal sealed class AccountActivity(IAuditTrail audit) : IAccountActivity
{
    public int PageSize => 50;

    public async Task<IReadOnlyList<ActivityRecord>> ListAsync(Guid userId, long? before, CancellationToken cancellationToken) =>
        [
            .. from record in await audit.ListForUserAsync(userId, before, PageSize, cancellationToken)
               select new ActivityRecord(record.Id, record.Timestamp, record.Action, record.ActorKind, record.IpAddress, record.UserAgent),
        ];
}
