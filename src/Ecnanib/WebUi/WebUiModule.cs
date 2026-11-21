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
    /// The path of the experience screen: the track record of each AI provider and of each model,
    /// from the closed trades of the user (FR-EXP-2, FR-UI-2).
    /// </summary>
    public const string ExperiencePath = "/experience";

    /// <summary>
    /// The path of the page of a model's experience record, which the catalog links each model
    /// to. The model is named by its id in the catalog, which stays the same for good.
    /// </summary>
    public static string ModelExperiencePath(Guid modelId) => $"/experience/models/{modelId}";

    /// <summary>
    /// The path of the page of the lessons of a user: what the models of the Risk Managers of
    /// their agents wrote after closed positions, which they read, disable, and delete (FR-EXP-8).
    /// </summary>
    public const string LessonsPath = "/experience/lessons";

    /// <summary>The name of the choice of an agent in the address of the page of the lessons.</summary>
    public const string LessonsAgentKey = "agent";

    /// <summary>The name of the choice of a page in the address of the page of the lessons.</summary>
    public const string LessonsPageKey = "page";

    /// <summary>The path of the page of the lessons, with the lessons of one agent alone, and with a page of them.</summary>
    public static string LessonsOfAgentPath(Guid? agentId, int page = 1)
    {
        var choices = new List<string>();

        if (agentId is { } agent)
        {
            choices.Add($"{LessonsAgentKey}={agent}");
        }

        if (page > 1)
        {
            choices.Add($"{LessonsPageKey}={page.ToString(CultureInfo.InvariantCulture)}");
        }

        return choices.Count == 0 ? LessonsPath : $"{LessonsPath}?{string.Join('&', choices)}";
    }

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

    /// <summary>
    /// The path of the page of the prompts of an agent: the templates that it uses now, which
    /// its user reads and cannot edit (FR-PRM-7).
    /// </summary>
    public static string AgentPromptsPath(Guid agentId) => $"{AgentPath(agentId)}/prompts";

    /// <summary>
    /// The path of the page of the risk checks of an agent: what the Risk Guard made of its
    /// plans, with the outcome of every rule (FR-RSK-9).
    /// </summary>
    public static string AgentRiskChecksPath(Guid agentId) => $"{AgentPath(agentId)}/risk-checks";

    /// <summary>
    /// The path of the page of the cycles of an agent: each cycle that was recorded, with how it
    /// ended (FR-CYC-10).
    /// </summary>
    public static string AgentCyclesPath(Guid agentId) => $"{AgentPath(agentId)}/cycles";

    /// <summary>
    /// The path of the page of one cycle of an agent, as a timeline: the summary of its data
    /// package, every role's output, the Risk Guard results, its orders, and its outcome
    /// (FR-CYC-10). The cycle is named by its id.
    /// </summary>
    public static string AgentCyclePath(Guid agentId, Guid cycleId) => $"{AgentCyclesPath(agentId)}/{cycleId}";

    /// <summary>
    /// The path of the page a user runs a test cycle of an agent on: one cycle, on demand, that
    /// is recorded and places no orders (FR-CYC-12).
    /// </summary>
    public static string AgentTestCyclePath(Guid agentId) => $"{AgentPath(agentId)}/test-cycle";

    /// <summary>The path of the page a user resets the lessons of an agent on: every one of them is deleted (FR-EXP-8).</summary>
    public static string ResetAgentLessonsPath(Guid agentId) => $"{AgentPath(agentId)}/lessons/reset";

    /// <summary>The path of the page a user deletes one lesson of an agent on (FR-EXP-8). The lesson is named by the id of its position.</summary>
    public static string DeleteAgentLessonPath(Guid agentId, Guid lessonId) => $"{AgentPath(agentId)}/lessons/{lessonId}/delete";

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
