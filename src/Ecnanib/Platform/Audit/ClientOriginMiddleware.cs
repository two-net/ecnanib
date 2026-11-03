namespace Ecnanib.Platform.Audit;

/// <summary>Sets the client's address and user agent for an HTTP request. It runs after UseForwardedHeaders.</summary>
internal sealed class ClientOriginMiddleware(RequestDelegate next, ClientOriginContext origin)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = origin.Begin(context);

        await next(context);
    }
}
