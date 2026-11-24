using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Ecnanib.WebUi.Live;

/// <summary>
/// Whether the connection of a page that follows is up (FR-NTF-2). The connection is the one
/// push channel from the application to the browser (NFR-ARC-6), and a browser that sleeps, or
/// loses its network, loses it for a while: the framework keeps the page for it meanwhile, and
/// nothing can be asked of the browser until it is back. See docs/live-updates.md.
/// </summary>
/// <remarks>
/// There is one for each connection, which the framework tells when the connection goes and
/// comes back. It is the one thing of the application that lives as long as a connection does:
/// everything else lives as long as the application, or as long as one request.
/// </remarks>
public sealed class LiveCircuit : CircuitHandler
{
    // A page that is being written has no connection yet, and asks nothing of a browser. A
    // connection that is made is up until the framework says otherwise.
    private volatile bool _connected = true;

    /// <summary>Whether the connection is up.</summary>
    public bool IsConnected => _connected;

    /// <summary>Told when the connection is up again, by whoever of the framework noticed.</summary>
    public event Action? Up;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = true;
        Up?.Invoke();

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = false;

        return Task.CompletedTask;
    }
}
