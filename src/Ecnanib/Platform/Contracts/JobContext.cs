namespace Ecnanib.Platform.Contracts;

/// <summary>What a job run knows about itself, beyond the token it was given.</summary>
public sealed class JobContext(CancellationToken stopping)
{
    /// <summary>
    /// Fires as soon as the application begins to stop, and whenever the run's own token fires. Check
    /// it between steps, and start nothing new once it has fired. Pass it only to work that's safe to
    /// abandon, such as an AI call or a purge batch, and never to an exchange call (NFR-REL-04).
    /// </summary>
    public CancellationToken Stopping { get; } = stopping;

    /// <summary>
    /// A summary to store with the run. Keep it to counts and IDs, because administrators can read it
    /// and account deletion doesn't reach it. It's scrubbed before it's stored.
    /// </summary>
    public string? Details { get; set; }
}
