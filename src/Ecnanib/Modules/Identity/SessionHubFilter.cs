using Ecnanib.Modules.Identity.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Holds circuits to their sessions. A circuit never passes through HTTP, and the framework attaches
/// any connection that holds its secret, then gives the circuit that connection's user without
/// re-initializing its components. So a circuit can only be rejoined by the session that started it,
/// which means closing a session's connections ends all access to its circuits. Everyone else gets
/// the false that makes a client reload the page.
/// <para>
/// The method names are the framework's own and aren't public. CircuitAccessTests fails if they change.
/// Refusals return rather than throw, because a throw here logs an Error, and an ended session is
/// routine. See Sessions in docs/architecture.md.
/// </para>
/// </summary>
internal sealed class SessionHubFilter(
    Sessions sessions,
    SessionConnections connections,
    IdentitySettings settings,
    ILogger<SessionHubFilter> logger) : IHubFilter
{
    private const string StartCircuit = nameof(StartCircuit);
    private const string ResumeCircuit = nameof(ResumeCircuit);
    private const string ConnectCircuit = nameof(ConnectCircuit);

    /// <summary>What a person does. Render acknowledgements aren't here, or a live page would never go idle.</summary>
    private const string BeginInvokeDotNetFromJS = nameof(BeginInvokeDotNetFromJS);
    private const string OnLocationChanged = nameof(OnLocationChanged);

    private static readonly object NextActivityCheck = new();

    /// <summary>Added before it's checked, so that a revocation after the cookie check sees it, or this sees that.</summary>
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (SessionOf(context.Context) is { } sessionId)
        {
            connections.Add(sessionId, context.Context);

            await IsAliveAsync(context.Context, sessionId, activity: false);
        }

        await next(context);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (SessionOf(context.Context) is { } sessionId)
        {
            connections.Remove(sessionId, context.Context);
        }

        return next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var connection = invocationContext.Context;
        var sessionId = SessionOf(connection);

        switch (invocationContext.HubMethodName)
        {
            case ConnectCircuit:
                return sessionId is { } joining
                    && invocationContext.HubMethodArguments[0] is string secret
                    && connections.OwnsCircuit(joining, secret)
                        ? await next(invocationContext)
                        : false;

            case StartCircuit or ResumeCircuit:
                var started = await next(invocationContext);

                if (sessionId is { } owner && started is string newSecret)
                {
                    connections.AddCircuit(owner, newSecret);
                }

                return started;

            case BeginInvokeDotNetFromJS or OnLocationChanged:
                return sessionId is { } active && !await RecordActivityAsync(connection, active) ? null : await next(invocationContext);

            default:
                return await next(invocationContext);
        }
    }

    private static Guid? SessionOf(HubCallerContext connection) =>
        connection.User is { } user ? AccountClaims.SessionId(user) : null;

    /// <summary>One check per touch interval, per connection, or typing would be a query per key.</summary>
    private async Task<bool> RecordActivityAsync(HubCallerContext connection, Guid sessionId)
    {
        var now = DateTimeOffset.UtcNow;

        if (connection.Items.TryGetValue(NextActivityCheck, out var due) && now < (DateTimeOffset)due!)
        {
            return true;
        }

        connection.Items[NextActivityCheck] = now + settings.SessionTouchInterval;

        return await IsAliveAsync(connection, sessionId, activity: true);
    }

    /// <summary>
    /// Ends a dead session. A failed check only closes this connection, because the session may be
    /// fine, and if it is, the client can rejoin. A throw would also skip OnDisconnectedAsync.
    /// </summary>
    private async Task<bool> IsAliveAsync(HubCallerContext connection, Guid sessionId, bool activity)
    {
        try
        {
            if (await sessions.SeenAsync(sessionId, activity, connection.ConnectionAborted))
            {
                return true;
            }

            connections.End([sessionId]);

            return false;
        }
        catch (OperationCanceledException) when (connection.ConnectionAborted.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Closed a hub connection because its session couldn't be checked");
            connection.Abort();

            return false;
        }
    }
}
