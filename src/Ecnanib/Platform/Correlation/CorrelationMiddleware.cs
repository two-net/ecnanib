using Ecnanib.Platform.Contracts;

namespace Ecnanib.Platform.Correlation;

/// <summary>Gives each HTTP request an ID, and returns it so that a user can quote it.</summary>
internal sealed class CorrelationMiddleware(RequestDelegate next, ICorrelationContext correlation)
{
    private const string Header = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = correlation.Begin();

        // A bodyless response can start after this method returns, when the scope has ended.
        var id = correlation.CurrentId;

        // Not set directly, because the exception handler clears the headers before it responds.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        await next(context);
    }
}
