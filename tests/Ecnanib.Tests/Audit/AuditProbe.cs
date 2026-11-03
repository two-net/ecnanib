using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Ecnanib.Tests.Audit;

/// <summary>
/// Writes an audit record from inside a circuit, the way a page's event handler would, so a test can
/// see what a circuit's work inherits. A static method can't take the application, so each call
/// registers it under a key that travels as the argument. Public, because Blazor only invokes public methods.
/// </summary>
public static class AuditProbe
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Call> Calls = new();

    /// <summary>
    /// The records the call wrote, oldest first: one from the circuit, then, with <paramref name="alsoInJob"/>,
    /// one from a job run the circuit started.
    /// </summary>
    internal static async Task<List<AuditRow>> RecordFromCircuitAsync(AppFactory app, CircuitClient connection, bool alsoInJob = false)
    {
        var key = Guid.NewGuid().ToString("N");
        var call = new Call(app.Services, Guid.NewGuid(), alsoInJob);

        Calls[key] = call;

        try
        {
            await connection.InvokeDotNetAsync("Ecnanib.Tests", nameof(WriteAuditRecord), $"[\"{key}\"]");
            await call.Done.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

            return await AuditRows.OfUserAsync(app.ConnectionString(), call.UserId, TestContext.Current.CancellationToken);
        }
        finally
        {
            Calls.TryRemove(key, out _);
        }
    }

    [JSInvokable]
    public static async Task WriteAuditRecord(string key)
    {
        var call = Calls[key];

        try
        {
            await WriteAsync(call, CancellationToken.None);

            if (call.AlsoInJob)
            {
                var status = await call.Services.GetRequiredService<IJobRunner>().RunAsync(
                    "audit-probe",
                    Deadline,
                    (_, cancellationToken) => WriteAsync(call, cancellationToken));

                Assert.Equal(JobStatus.Succeeded, status);
            }

            call.Done.TrySetResult();
        }
        catch (Exception exception)
        {
            call.Done.TrySetException(exception);
        }
    }

    private static async Task WriteAsync(Call call, CancellationToken cancellationToken)
    {
        await using var context = await call.Services
            .GetRequiredService<IDbContextFactory<EcnanibDbContext>>()
            .CreateDbContextAsync(cancellationToken);

        call.Services.GetRequiredService<IAuditTrail>().Add(context, AuditEntry.ByUser(AuditAction.SessionRevoked, call.UserId));
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed record Call(IServiceProvider Services, Guid UserId, bool AlsoInJob)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
