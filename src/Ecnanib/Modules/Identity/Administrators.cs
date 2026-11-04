using System.Security.Cryptography;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

internal sealed class Administrators(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IAuditTrail audit,
    IdentitySettings settings,
    ILogger<Administrators> logger) : IAdministrators
{
    public async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        await FindAddressAsync(userId, cancellationToken) is { EmailVerifiedAt: not null } address
            && settings.AdministratorEmails.Contains(address.NormalizedEmail);

    public async Task<bool> CanConfirmAsync(Guid userId, CancellationToken cancellationToken) =>
        !settings.EmailEnabled
            && await FindAddressAsync(userId, cancellationToken) is { EmailVerifiedAt: null } address
            && settings.AdministratorEmails.Contains(address.NormalizedEmail);

    public async Task<bool> ConfirmAsync(Guid userId, string secret, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        if (settings.BootstrapSecret is not { } expected || !await CanConfirmAsync(userId, cancellationToken))
        {
            return false;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!Matches(secret, expected))
        {
            logger.LogWarning("An attempt to confirm an administrator used the wrong secret");

            audit.Add(context, AuditEntry.ByUser(AuditAction.AdministratorConfirmationFailed, userId));
            await context.SaveChangesAsync(cancellationToken);

            return false;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await context.Set<User>()
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.EmailVerifiedAt, DateTimeOffset.UtcNow),
                cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.AdministratorConfirmed, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Confirmed an administrator with the bootstrap secret");

        return true;
    }

    public async Task<IReadOnlyList<Guid>> ListAsync(CancellationToken cancellationToken)
    {
        if (settings.AdministratorEmails.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var listed = settings.AdministratorEmails.ToList();

        return await context.Set<User>()
            .Where(user => user.EmailVerifiedAt != null && listed.Contains(user.NormalizedEmail))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Hashed first, because FixedTimeEquals returns at once when the lengths differ.</summary>
    private static bool Matches(string secret, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private async Task<Address?> FindAddressAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new Address(user.NormalizedEmail, user.EmailVerifiedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private sealed record Address(string NormalizedEmail, DateTimeOffset? EmailVerifiedAt);
}
