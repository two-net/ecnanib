using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Readiness (NFR-OBS-01). The endpoint is public and shares its connection pool with cycles and the
/// log sink, so however many callers arrive, one probe runs at a time. Callers see only the data.
/// The description and exception are for the framework's log event, where masking applies, so they
/// may name migrations and hosts. It must be registered as a singleton, or each run gets its own
/// instance and the probes stop being shared.
/// </summary>
internal sealed class DatabaseHealthCheck(IDbContextFactory<EcnanibDbContext> contextFactory) : IHealthCheck
{
    private const string Connected = "connected";
    private const string Unreachable = "unreachable";
    private const string Unknown = "unknown";

    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Later than the probe's own, so a probe that can stop reports how far it got. One that can't is
    /// left running, and later callers join it: Npgsql's cancellation can itself wait on a silent server.
    /// </summary>
    private static readonly TimeSpan CallerDeadline = TimeSpan.FromSeconds(4);

    private readonly Lock _lock = new();
    private Task<HealthCheckResult>? _running;

    /// <summary>Ignores the caller's token, because one client leaving mustn't cancel a probe others are waiting on.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Task<HealthCheckResult> probe;

        lock (_lock)
        {
            probe = _running ??= Task.Run(ProbeAsync);
        }

        try
        {
            return await probe.WaitAsync(CallerDeadline);
        }
        catch (TimeoutException exception)
        {
            return Failed("The database probe didn't finish in time", exception, Unreachable, Unknown);
        }
    }

    private async Task<HealthCheckResult> ProbeAsync()
    {
        using var deadline = new CancellationTokenSource(ProbeDeadline);
        var database = Unreachable;

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(deadline.Token);

            // First, because reading the history treats a missing database as no migrations applied.
            await context.Database.ExecuteSqlAsync($"SELECT 1", deadline.Token);
            database = Connected;

            var applied = (await context.Database.GetAppliedMigrationsAsync(deadline.Token)).ToList();
            var known = context.Database.GetMigrations().ToList();

            var ahead = applied.Except(known).ToList();
            var pending = known.Except(applied).ToList();

            if (ahead.Count > 0)
            {
                return Failed($"Migrations this build doesn't include: {string.Join(", ", ahead)}", null, database, "ahead");
            }

            if (pending.Count > 0)
            {
                return Failed($"Pending migrations: {string.Join(", ", pending)}", null, database, "pending");
            }

            return HealthCheckResult.Healthy(data: Data(database, "current"));
        }
        catch (Exception exception)
        {
            // Broad because whatever went wrong, the answer is "not ready", and a throw would lose
            // the data: the framework only keeps the exception's message.
            return Failed("The database probe failed", exception, database, Unknown);
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
            }
        }
    }

    private static HealthCheckResult Failed(string description, Exception? exception, string database, string migrations) =>
        HealthCheckResult.Unhealthy(description, exception, Data(database, migrations));

    private static Dictionary<string, object> Data(string database, string migrations) =>
        new() { ["database"] = database, ["migrations"] = migrations };
}
