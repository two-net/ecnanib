using Microsoft.AspNetCore.SignalR;

namespace Ecnanib.Platform.Audit;

/// <summary>
/// Gives each hub call the client's address and user agent, so an action on a circuit is audited with
/// them. The hub method starts the circuit's work, which carries the value with it.
/// <para>
/// The connection's HttpContext is the WebSocket request, or a copy of the first long polling request,
/// and either has been through UseForwardedHeaders. SignalR also happens to run a connection's calls
/// in the execution context of the request that opened it, so the middleware's value would arrive
/// anyway, but nothing documents that, and GetHttpContext is the API for it. It's registered before
/// Identity's filter, so it runs outside it.
/// </para>
/// </summary>
internal sealed class ClientOriginHubFilter(ClientOriginContext origin) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var scope = invocationContext.Context.GetHttpContext() is { } http ? origin.Begin(http) : origin.Clear();

        return await next(invocationContext);
    }
}
