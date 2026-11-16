using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Shared;
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

    /// <summary>The path of the page that lists the keys a user has stored (FR-KEY-2).</summary>
    public const string KeysPath = "/account/keys";

    /// <summary>The path of the page a user stores their key for an AI provider on, as a new one or in place of the stored one.</summary>
    public static string AiKeyPath(AiProviderCode provider) => $"{KeysPath}/ai/{Uri.EscapeDataString(provider.Value)}";

    /// <summary>The path of the page a user deletes their key for an AI provider on.</summary>
    public static string DeleteAiKeyPath(AiProviderCode provider) => $"{AiKeyPath(provider)}/delete";

    /// <summary>The path of the page a user stores their Binance credential for an environment on: <c>demo</c> or <c>live</c>.</summary>
    public static string ExchangeKeyPath(TradingMode environment) =>
        $"{KeysPath}/binance/{environment.ToString().ToLowerInvariant()}";

    /// <summary>The path of the page a user deletes their Binance credential for an environment on.</summary>
    public static string DeleteExchangeKeyPath(TradingMode environment) => $"{ExchangeKeyPath(environment)}/delete";

    /// <summary>The path of the page that lists the catalog of models by provider (FR-CAT-10).</summary>
    public const string CatalogPath = "/catalog";

    /// <summary>
    /// The path of the page of a model's experience record, which the catalog links each model
    /// to. The model is named by its id in the catalog, which stays the same for good.
    /// </summary>
    public static string ModelExperiencePath(Guid modelId) => $"/experience/models/{modelId}";

    /// <summary>The path of the page that lists the agents of a user (FR-AGT-1).</summary>
    public const string AgentsPath = "/agents";

    /// <summary>The path of the page a user creates an agent on.</summary>
    public const string NewAgentPath = "/agents/new";

    /// <summary>The path of the page a user changes the settings of an agent on. The agent is named by its id.</summary>
    public static string AgentPath(Guid agentId) => $"{AgentsPath}/{agentId}";

    /// <summary>The path of the page a user deletes an agent on.</summary>
    public static string DeleteAgentPath(Guid agentId) => $"{AgentPath(agentId)}/delete";

    /// <summary>
    /// The path of the page of the state of an agent: what state it is in and why, the controls
    /// that start, pause, and resume it, and the changes of its state (FR-AGT-10).
    /// </summary>
    public static string AgentStatePath(Guid agentId) => $"{AgentPath(agentId)}/state";

    /// <summary>The path of the page a user stops an agent on, and chooses what happens to its open position (FR-AGT-11).</summary>
    public static string StopAgentPath(Guid agentId) => $"{AgentPath(agentId)}/stop";

    /// <summary>The path of the page a user closes the open position of an agent on, at any time (FR-AGT-12).</summary>
    public static string CloseAgentPositionPath(Guid agentId) => $"{AgentPath(agentId)}/close-position";

    /// <summary>
    /// The path of the page a user switches an agent from Demo to Live on, with the confirmation
    /// of the risk and after a fresh proof of who they are (FR-AGT-7).
    /// </summary>
    public static string SwitchAgentToLivePath(Guid agentId) => $"{AgentPath(agentId)}/live";

    /// <summary>The path of the page a user switches an agent from Live back to Demo on.</summary>
    public static string SwitchAgentToDemoPath(Guid agentId) => $"{AgentPath(agentId)}/demo";

    /// <summary>
    /// The path of the page that asks once more before an agent is started, paused, or resumed:
    /// <c>start</c>, <c>pause</c>, or <c>resume</c>. A control of an agent in Live leads to it
    /// (FR-UI-3). Stopping an agent has a page of its own.
    /// </summary>
    public static string ConfirmAgentControlPath(Guid agentId, AgentControl control) =>
        $"{AgentPath(agentId)}/confirm/{control.ToString().ToLowerInvariant()}";

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

        // The framework would say on its pages that this site may frame them. Whether the
        // application may be shown in a frame is said in one place, for every answer: no site
        // may. See BrowserDefenses in Platform.
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

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
