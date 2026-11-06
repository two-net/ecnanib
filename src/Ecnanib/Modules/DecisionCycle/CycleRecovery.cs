using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// At startup, ends the cycles a crash left running as Interrupted, as the job runner does with its runs. With one
/// instance (ASM-06), a cycle still running when the application starts belongs to a process that's gone. It works in
/// <c>StartAsync</c>, which is after the migrations and before the first scheduled cycle, because the scheduler waits
/// for the application to have started. It also takes <see cref="DecisionCycleSettings"/>, so an invalid one stops
/// startup before any migration runs (CFG-03).
/// </summary>
internal sealed class CycleRecovery(
    DecisionCycleSettings settings,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ILogger<CycleRecovery> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "An agent goes to Error after {CycleErrorsBeforePause} decision cycles in a row without a decision, where 0 means never",
            settings.CycleErrorsBeforePause);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var abandoned = context.Set<DecisionCycleRow>().Where(cycle => cycle.Outcome == null);

        // Their roles that never finished never will.
        await context.Set<CycleRoleRow>()
            .Where(role => abandoned.Any(cycle => cycle.Id == role.CycleId)
                && (role.State == RoleState.Queued || role.State == RoleState.Running))
            .ExecuteUpdateAsync(setters => setters.SetProperty(role => role.State, RoleState.Skipped), cancellationToken);

        // Their end stays null, because nobody knows when they stopped.
        var count = await abandoned.ExecuteUpdateAsync(
            setters => setters.SetProperty(cycle => cycle.Outcome, CycleOutcome.Interrupted),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (count > 0)
        {
            logger.LogWarning("Marked {Count} decision cycles a stopped process left running as interrupted", count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
