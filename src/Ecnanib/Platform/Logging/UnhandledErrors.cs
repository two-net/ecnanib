using System.Security.Cryptography;
using Microsoft.AspNetCore.Diagnostics;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Records errors that nothing handled. Each one is logged in full, with the correlation
/// properties of where it happened, under a reference id. The user is shown a plain message and
/// that id, and never the error itself (NFR-LOG-6).
/// </summary>
public sealed class UnhandledErrors(ILogger<UnhandledErrors> logger)
{
    // Letters and digits that are not mistaken for one another when read out.
    private const string ReferenceCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly object RequestItem = new();

    /// <summary>
    /// Logs <paramref name="error"/> and returns the reference id to show the user. The event
    /// carries it as the property <c>ReferenceId</c>.
    /// </summary>
    public string Report(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var reference = RandomNumberGenerator.GetString(ReferenceCharacters, 10);

        logger.LogError(error, "An unhandled error was shown to the user as reference {ReferenceId}", reference);

        return reference;
    }

    /// <summary>
    /// Logs the error that failed a request, once: whoever asks again for the same request gets
    /// the same reference id.
    /// </summary>
    public string Report(HttpContext request, Exception error)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ReferenceOf(request) is { } reported)
        {
            return reported;
        }

        var reference = Report(error);
        request.Items[RequestItem] = reference;

        return reference;
    }

    /// <summary>The reference id of the error that failed <paramref name="request"/>, or null when none did.</summary>
    public static string? ReferenceOf(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Items.TryGetValue(RequestItem, out var reference) ? reference as string : null;
    }
}

/// <summary>
/// Records the error of a failed request before anything answers it. It writes no response: the
/// API and the error page do that, each in its own form, with the reference id recorded here.
/// </summary>
internal sealed class UnhandledExceptionRecorder(UnhandledErrors errors) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        errors.Report(httpContext, exception);

        return ValueTask.FromResult(false);
    }
}
