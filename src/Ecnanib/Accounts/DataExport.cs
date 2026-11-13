using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Reads what is stored about a user, for them to keep (NFR-SEC-9). See docs/accounts.md.</summary>
/// <remarks>
/// It reads the rows that Accounts keeps. A component above Accounts keeps its own, which
/// Accounts cannot read: it hands them over as an <see cref="IExportedPart"/>.
/// </remarks>
internal sealed class DataExport(
    IDbContextFactory<EcnanibDbContext> contexts,
    IEnumerable<IExportedPart> parts,
    TimeProvider clock,
    ILogger<DataExport> logger) : IDataExport
{
    public async Task<ExportedData?> ExportAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Only what says something about the user. The hash of the password, the key of
        // two-factor authentication, and the hashes of tokens are never read for an export.
        var account = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.Email,
                user.RegisteredAt,
                user.EmailVerifiedAt,
                user.TwoFactorEnabledAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            // The account was deleted, on another browser, since this request was signed in.
            return null;
        }

        // How many, and not which: a recovery code is stored as a hash, which tells its user nothing.
        var recoveryCodesLeft = await context.Set<RecoveryCode>()
            .OwnedBy(userId)
            .CountAsync(cancellationToken);

        var acceptances = await context.Set<LegalAcceptance>()
            .OwnedBy(userId)
            .OrderBy(acceptance => acceptance.AcceptedAt)
            .ThenBy(acceptance => acceptance.Document)
            .ThenBy(acceptance => acceptance.Version)
            .Select(acceptance => new ExportedAcceptance(acceptance.Document, acceptance.Version, acceptance.AcceptedAt))
            .ToListAsync(cancellationToken);

        var sessions = await context.Set<Session>()
            .OwnedBy(userId)
            .OrderBy(session => session.CreatedAt)
            .ThenBy(session => session.Id)
            .Select(session => new ExportedSession(session.CreatedAt, session.LastActiveAt))
            .ToListAsync(cancellationToken);

        // The id of an event starts with the time it was recorded at, so this is the order they
        // were recorded in.
        var events = await context.Set<AuditEvent>()
            .OwnedBy(userId)
            .OrderBy(recorded => recorded.Id)
            .Select(recorded => new ExportedAuditEvent(recorded.Action, recorded.Target, recorded.OccurredAt, recorded.Origin))
            .ToListAsync(cancellationToken);

        // What the components above Accounts keep for the user, in the order they are registered in.
        var others = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            others.Add(part.Section, await part.ReadAsync(context, userId, cancellationToken));
        }

        logger.LogInformation("User {UserId} exported their data", userId);

        return new ExportedData(
            clock.GetUtcNow(),
            new ExportedAccount(
                userId,
                account.Email,
                account.RegisteredAt,
                account.EmailVerifiedAt,
                new ExportedTwoFactor(account.TwoFactorEnabledAt is not null, account.TwoFactorEnabledAt, recoveryCodesLeft)),
            acceptances,
            sessions,
            events)
        {
            Parts = others,
        };
    }
}
