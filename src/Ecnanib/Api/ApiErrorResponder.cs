using Ecnanib.Platform.Logging;
using Microsoft.AspNetCore.Diagnostics;

namespace Ecnanib.Api;

/// <summary>
/// Answers an API request that failed with an unhandled error: a plain message and the reference
/// id the error was logged under, as problem JSON, and nothing of the error itself (NFR-LOG-6).
/// </summary>
internal sealed class ApiErrorResponder(UnhandledErrors errors) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // By now the request points at the error page. The path it came in on is kept in this feature.
        var path = httpContext.Features.Get<IExceptionHandlerPathFeature>()?.Path;
        if (path is null || !new PathString(path).StartsWithSegments(ApiEndpoints.Prefix))
        {
            return false;
        }

        var reference = errors.Report(httpContext, exception);

        var problem = TypedResults.Problem(
            title: "Something went wrong.",
            detail: $"The error has been recorded. Report it with reference {reference} if it keeps happening.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["referenceId"] = reference });

        await problem.ExecuteAsync(httpContext);

        return true;
    }
}
