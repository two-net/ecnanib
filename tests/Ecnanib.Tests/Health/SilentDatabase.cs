using System.Data.Common;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Health;

/// <summary>
/// Stands in for a server that has stopped answering. Once muted, a command waits until released
/// and ignores cancellation, which no real database can be made to do on demand: a blocked query
/// on PostgreSQL still responds to a cancel request.
/// </summary>
internal sealed class SilentDatabase : DbCommandInterceptor
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _commands;
    private bool _muted;

    public int Commands => Volatile.Read(ref _commands);

    public void AddTo(IServiceCollection services) =>
        services.ConfigureDbContext<EcnanibDbContext>(
            options => options.AddInterceptors(this),
            ServiceLifetime.Singleton);

    public void Mute() => Volatile.Write(ref _muted, true);

    public void Release()
    {
        Volatile.Write(ref _muted, false);
        _released.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _muted))
        {
            Interlocked.Increment(ref _commands);
            await _released.Task;
        }

        return result;
    }
}
