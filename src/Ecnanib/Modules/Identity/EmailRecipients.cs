using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>Only verified addresses, because anyone can register an address that isn't theirs.</summary>
internal sealed class EmailRecipients(IDbContextFactory<EcnanibDbContext> contextFactory, IdentitySettings settings) : IEmailRecipients
{
    public async Task<IReadOnlyList<EmailRecipient>> FindAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ids = userIds.Distinct().ToList();

        var rows = await (
            from user in context.Set<User>()
            where ids.Contains(user.Id) && user.EmailVerifiedAt != null
            join preference in context.Set<UserPreference>() on user.Id equals preference.UserId into preferences
            from preference in preferences.DefaultIfEmpty()
            select new { user.Id, user.Email, user.NormalizedEmail, Stored = preference == null ? null : preference.EmailNotifications })
            .ToListAsync(cancellationToken);

        return
        [
            .. from row in rows
               let administrator = settings.AdministratorEmails.Contains(row.NormalizedEmail)
               select new EmailRecipient(row.Id, row.Email, UserPreference.EffectiveEmailNotifications(row.Stored, administrator)),
        ];
    }
}
