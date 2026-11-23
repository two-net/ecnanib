using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Notifications;

/// <summary>
/// That a user opted in to email for critical events, as a row of the <c>email_opt_ins</c>
/// table (FR-NTF-3). See "Email for critical events" in docs/notifications.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row says that its user is sent email,</b> and a user with no row is sent none: no user
/// is until they choose so, and a user who turns it off has no row again. So what sends an email
/// for a notification asks for the row, and sends nothing without it.
/// </para>
/// <para>
/// <b>It is the user's own choice, and a preference of theirs.</b> It says nothing of who the
/// user is: an id and a time. It is in the export of the user's data.
/// </para>
/// </remarks>
/// <param name="userId">The user who opted in.</param>
/// <param name="optedInAt">When they did.</param>
public sealed class EmailOptIn(Guid userId, DateTimeOffset optedInAt) : IUserOwned
{
    public Guid UserId { get; } = userId;

    public DateTimeOffset OptedInAt { get; } = optedInAt;
}

internal sealed class EmailOptInConfiguration : IEntityTypeConfiguration<EmailOptIn>
{
    public void Configure(EntityTypeBuilder<EmailOptIn> builder)
    {
        builder.ToTable("email_opt_ins");

        // A user has opted in or has not: the row is found by its user, and there is one at
        // the most.
        builder.HasKey(optIn => optIn.UserId);

        builder.Property(optIn => optIn.UserId).ValueGeneratedNever();
        builder.Property(optIn => optIn.OptedInAt);

        // The choice belongs to the account and goes with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(optIn => optIn.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Whether the signed-in user is sent an email for a critical event, which is the user's own
/// choice: users can opt in to email for critical events (FR-NTF-3). See "Email for critical
/// events" in docs/notifications.md.
/// </summary>
/// <remarks>
/// The user is the one of the session, and the choice of anyone else is neither read nor changed
/// (NFR-SEC-3). The choice exposes no key and moves no money, so it asks for no proof of
/// identity, and is in no event of the audit trail.
/// </remarks>
public interface IEmailOptIn
{
    /// <summary>
    /// Whether the signed-in user opted in, and the address that an email goes to. No user has
    /// opted in until they choose so.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    Task<EmailOptInState> StateAsync(HttpContext request);

    /// <summary>
    /// Opts the signed-in user in to email for critical events, or out of it. It holds for the
    /// notifications that are raised from then on, and a user who opts out is sent nothing that
    /// was still to be sent. A choice that is made twice changes nothing the second time.
    /// </summary>
    /// <param name="request">The request, which says who is signed in.</param>
    /// <param name="on">Whether the user is to be sent email.</param>
    Task SetAsync(HttpContext request, bool on);
}

/// <summary>Where a user stands with email for critical events.</summary>
/// <param name="On">Whether the user opted in.</param>
/// <param name="Address">The address of the account, which an email goes to, or null when the account is gone.</param>
/// <param name="AddressVerified">
/// Whether the address is verified. No email is sent to an address that is not: nothing says
/// yet that it is the user's own.
/// </param>
public sealed record EmailOptInState(bool On, string? Address, bool AddressVerified);

/// <summary>Reads and stores who opted in to email for critical events.</summary>
internal sealed class StoredEmailOptIn(
    IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock, ILogger<StoredEmailOptIn> logger) : IEmailOptIn
{
    public async Task<EmailOptInState> StateAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: the row of another user says nothing here.
        var on = await context.Set<EmailOptIn>().AsNoTracking().OwnedBy(userId).AnyAsync(cancellationToken);

        // The account of the session, and no other. One that was deleted since the request was
        // signed in has no address.
        var account = await context.Set<User>()
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Email, Verified = user.EmailVerifiedAt != null })
            .SingleOrDefaultAsync(cancellationToken);

        return new EmailOptInState(on, account?.Email, account?.Verified ?? false);
    }

    public async Task SetAsync(HttpContext request, bool on)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        if (!on)
        {
            // By owner: the row of the user, and no other.
            if (await context.Set<EmailOptIn>().OwnedBy(userId).ExecuteDeleteAsync(cancellationToken) > 0)
            {
                logger.LogInformation("User {UserId} opted out of email for critical events", userId);
            }

            return;
        }

        var now = clock.GetUtcNow();

        // Of two forms that are sent at the same time, the second finds the row of the first,
        // and writes nothing: a user has one row at the most. The statement names the owner
        // itself.
        var written = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO email_opt_ins (user_id, opted_in_at)
            VALUES ({userId}, {now})
            ON CONFLICT (user_id) DO NOTHING
            """,
            cancellationToken);

        if (written > 0)
        {
            logger.LogInformation("User {UserId} opted in to email for critical events", userId);
        }
    }
}

/// <summary>
/// That a user opted in to email for critical events, as the export of the user's data holds it
/// (NFR-SEC-9, FR-NTF-3).
/// </summary>
/// <param name="OptedInAt">When the user opted in.</param>
public sealed record ExportedEmailOptIn(DateTimeOffset OptedInAt);

/// <summary>
/// Whether a user opted in to email for critical events, for the export of their data
/// (NFR-SEC-9, FR-NTF-3): a preference of the user, which is in the export. The section has one
/// row for a user who opted in, and none for a user who did not.
/// </summary>
internal sealed class EmailOptInExport : IExportedPart
{
    public string Section => "emailOptIn";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // The row of the user, where there is one: asked for by owner.
        var optIns = await context.Set<EmailOptIn>()
            .AsNoTracking()
            .OwnedBy(userId)
            .ToListAsync(cancellationToken);

        return [.. optIns.Select(optIn => new ExportedEmailOptIn(optIn.OptedInAt))];
    }
}
