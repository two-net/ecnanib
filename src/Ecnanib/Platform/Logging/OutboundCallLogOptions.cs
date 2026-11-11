namespace Ecnanib.Platform.Logging;

/// <summary>
/// What the log of outbound calls records besides duration and outcome (NFR-LOG-5). The component
/// that knows an external system adds what that system reports.
/// </summary>
public sealed class OutboundCallLogOptions
{
    /// <summary>
    /// The response headers that report usage, such as the request weight an exchange has counted,
    /// each by the start of its name. A matching header is logged with the call.
    /// </summary>
    public IList<string> UsageHeaderPrefixes { get; } = [];
}
