namespace Ecnanib.WebUi;

/// <summary>Web UI: screens, themes, animation, live updates (SRS 3.1).</summary>
public static class WebUiModule
{
    public static IServiceCollection AddWebUi(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        return services;
    }

    public static IEndpointRouteBuilder MapWebUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return endpoints;
    }
}
