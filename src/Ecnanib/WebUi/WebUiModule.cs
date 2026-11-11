using Ecnanib.Accounts;
using Microsoft.AspNetCore.Antiforgery;

namespace Ecnanib.WebUi;

/// <summary>Web UI: screens, themes, animation, live updates (SRS 3.1).</summary>
public static class WebUiModule
{
    /// <summary>The path that the sign-out button posts to.</summary>
    public const string SignOutPath = "/sign-out";

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
}
