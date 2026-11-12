using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Accounts;
using Microsoft.AspNetCore.Antiforgery;

namespace Ecnanib.WebUi;

/// <summary>Web UI: screens, themes, animation, live updates (SRS 3.1).</summary>
public static class WebUiModule
{
    /// <summary>The path that the sign-out button posts to.</summary>
    public const string SignOutPath = "/sign-out";

    /// <summary>The path of the download of what is stored about the signed-in user (NFR-SEC-9).</summary>
    public const string ExportPath = "/account/export";

    /// <summary>The path of the page a user deletes their account on (FR-ACC-9).</summary>
    public const string DeleteAccountPath = "/account/delete";

    /// <summary>The path of the page that says an account was deleted. It is a page for visitors: nobody is signed in by then.</summary>
    public const string AccountDeletedPath = "/account/deleted";

    // The export as a person reads it: indented, with the names of things and not their numbers.
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IServiceCollection AddWebUi(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // A page says who may open it, and a layout can show who is signed in.
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();

        return services;
    }

    public static IEndpointRouteBuilder MapWebUi(this IEndpointRouteBuilder endpoints)
    {
        // Not a page: the button is on every page, and the answer is a redirect.
        endpoints.MapPost(SignOutPath, SignOutAsync);

        // Not a page either: the answer is a file. A visitor is sent to sign in, as on a page.
        endpoints.MapGet(ExportPath, ExportAsync).RequireAuthorization();

        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return endpoints;
    }

    private static async Task<IResult> SignOutAsync(HttpContext request, IAntiforgery antiforgery, ISignIn signIn)
    {
        // A signed-in request must come from a page of this application. A request that is not
        // signed in has nothing to protect, and its page may be older than its session: the
        // token against request forgery of that page was made for the user it showed.
        if (request.User.Identity is { IsAuthenticated: true } && !await antiforgery.IsRequestValidAsync(request))
        {
            return Results.BadRequest();
        }

        await signIn.SignOutAsync(request);

        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> ExportAsync(HttpContext request, IDataExport export)
    {
        if (await export.ExportAsync(request) is not { } data)
        {
            // The account was deleted since the request was signed in, so nobody is signed in.
            return Results.Challenge();
        }

        // What is stored about a person is for that person, and not for whoever uses the
        // browser next: it is not kept by the browser, nor by anything on the way to it.
        request.Response.Headers.CacheControl = "no-store";

        // As a file to keep, named after the day it is of.
        return Results.File(
            JsonSerializer.SerializeToUtf8Bytes(data, ExportJson),
            "application/json",
            $"account-data-{data.ExportedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");
    }
}
