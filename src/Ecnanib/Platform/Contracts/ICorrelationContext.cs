namespace Ecnanib.Platform.Contracts;

/// <summary>
/// The correlation ID that links a request or cycle to everything it causes (NFR-OBS-02). HTTP
/// requests and circuit activity get one automatically. See docs/architecture.md.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>The ID of the work in progress, or null outside any. Audit records store this.</summary>
    string? CurrentId { get; }

    /// <summary>
    /// Starts a new ID for a cycle or job, which log events inherit, along with everything further
    /// down the async call. It's always new, because a timer or task started during a request
    /// captures that request's ID. Disposing restores the previous one.
    /// </summary>
    IDisposable Begin();
}
