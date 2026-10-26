using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Support;

/// <summary>Records a request to stop the host.</summary>
public sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public bool StopRequested => _stopping.IsCancellationRequested;

    public void StopApplication() => _stopping.Cancel();
}
