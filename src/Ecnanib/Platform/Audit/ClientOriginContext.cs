using Ecnanib.Platform.Contracts;

namespace Ecnanib.Platform.Audit;

/// <summary>The client behind the work in progress, as its address and user agent are stored.</summary>
internal sealed record ClientOrigin(string? IpAddress, string? UserAgent);

/// <summary>
/// Where the current request or circuit action came from (FR-AUD-02). It flows with the async call,
/// like the correlation ID. A circuit action has no request of its own, so ClientOriginHubFilter sets
/// it from the hub connection for every call, and the circuit's work inherits it.
/// </summary>
internal sealed class ClientOriginContext
{
    private readonly AsyncLocal<ClientOrigin?> _current = new();

    /// <summary>Null outside a request or circuit action.</summary>
    public ClientOrigin? Current => _current.Value;

    /// <summary>
    /// Read once, rather than keeping the HttpContext, which is reused after its request ends. Call it
    /// after UseForwardedHeaders, which is what makes RemoteIpAddress the client's.
    /// </summary>
    public IDisposable Begin(HttpContext context) =>
        Set(new ClientOrigin(
            ClientText.Address(context.Connection.RemoteIpAddress),
            ClientText.UserAgent(context.Request.Headers.UserAgent)));

    /// <summary>For a job run, which acts for nobody, even when a request started it.</summary>
    public IDisposable Clear() => Set(null);

    private Restore Set(ClientOrigin? origin)
    {
        var outer = _current.Value;
        _current.Value = origin;

        return new Restore(_current, outer);
    }

    private sealed class Restore(AsyncLocal<ClientOrigin?> current, ClientOrigin? outer) : IDisposable
    {
        public void Dispose() => current.Value = outer;
    }
}
