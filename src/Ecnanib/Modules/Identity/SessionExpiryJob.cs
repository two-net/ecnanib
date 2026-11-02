using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Nothing depends on this for a session to stop working: HTTP checks the timeouts on each request,
/// and a circuit on each user action. It closes tabs nobody is touching and keeps the table small.
/// </summary>
internal sealed class SessionExpiryJob(Sessions sessions) : IRecurringJob
{
    public string Name => "session-expiry";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(5));

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(1);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        context.Details = $"{await sessions.DeleteExpiredAsync(cancellationToken)} sessions";
}
