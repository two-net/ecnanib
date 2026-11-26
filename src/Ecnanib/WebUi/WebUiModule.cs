using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.WebUi.Exports;
using Ecnanib.WebUi.Layout;
using Ecnanib.WebUi.Live;
using Ecnanib.WebUi.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Ecnanib.WebUi;

/// <summary>Web UI: screens, themes, animation, live updates (SRS 3.1).</summary>
public static class WebUiModule
{
    /// <summary>
    /// The path of the start page: the landing page of a visitor, and where a user arrives after
    /// signing in (FR-UI-2).
    /// </summary>
    public const string HomePath = "/";

    /// <summary>The path of the page a visitor registers on.</summary>
    public const string RegisterPath = "/register";

    /// <summary>The path that the sign-out button posts to.</summary>
    public const string SignOutPath = "/sign-out";

    /// <summary>
    /// The path that the notice before a session ends sends "Stay signed in" to (NFR-USA-1).
    /// A request there is activity of its user, as any request of theirs is, and so the period
    /// of inactivity starts anew. It is no page. See <c>SessionNotice</c>.
    /// </summary>
    public const string StaySignedInPath = "/session/stay";

    /// <summary>
    /// The path of the page of the account of a user, which is the profile among the settings
    /// (FR-UI-2).
    /// </summary>
    public const string AccountPath = "/account";

    /// <summary>
    /// The path of the page of two-factor authentication, which is the security among the
    /// settings (FR-UI-2).
    /// </summary>
    public const string TwoFactorPath = "/account/two-factor";

    /// <summary>
    /// The path of the page of the appearance among the settings, where a user chooses the
    /// theme of their account: dark, light, or as their device is set (FR-UI-2, FR-THM-1).
    /// </summary>
    public const string AppearancePath = "/account/appearance";

    /// <summary>
    /// The path of the page of the appearance for a visitor, who chooses the theme of their
    /// device there: nobody needs to be signed in for it (FR-THM-3). The foot of a page of a
    /// visitor leads to it.
    /// </summary>
    public const string DeviceAppearancePath = "/appearance";

    /// <summary>
    /// The path of the operator status screen, which is for operators alone (FR-UI-2, FR-OPS-2).
    /// What it is to show is not built yet.
    /// </summary>
    public const string OperatorStatusPath = "/operator";

    /// <summary>The path of the download of what is stored about the signed-in user (NFR-SEC-9).</summary>
    public const string ExportPath = "/account/export";

    /// <summary>
    /// The path of the download of the trades of the signed-in user as CSV: the closed positions
    /// of every agent of theirs, each with the mode that it was of (FR-RPT-4, FR-BIN-11).
    /// </summary>
    public const string TradesExportPath = "/export/trades";

    /// <summary>
    /// The path of the download of the decisions of the signed-in user as CSV: the recorded
    /// cycles of every agent of theirs, each with the mode that it was of (FR-RPT-4, FR-BIN-11).
    /// </summary>
    public const string DecisionsExportPath = "/export/decisions";

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

    /// <summary>
    /// The path of the dashboard: every agent of a user at a glance, with total equity, profit
    /// and loss for the day, open positions, a status card per agent, recent decisions, and
    /// alerts, for Demo and for Live apart (FR-RPT-1, FR-UI-2).
    /// </summary>
    public const string DashboardPath = "/dashboard";

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

    /// <summary>
    /// The path of the platform-wide board: the same track record, aggregated across all users,
    /// which names none of them (FR-EXP-10). A user leaves the board there, and comes back to it.
    /// </summary>
    public const string BoardPath = "/experience/board";

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

    /// <summary>
    /// The path of the page of the AI usage of a user: the calls that the roles of their agents
    /// made, by day, agent, and provider, with their tokens and what they are estimated to cost
    /// (FR-RPT-3).
    /// </summary>
    public const string UsagePath = "/usage";

    /// <summary>The name of the choice of an agent in the address of the page of the AI usage.</summary>
    public const string UsageAgentKey = "agent";

    /// <summary>The name of the choice of a period in the address of the page of the AI usage.</summary>
    public const string UsagePeriodKey = "period";

    /// <summary>The path of the page of the AI usage, with the usage of one agent alone.</summary>
    public static string UsageOfAgentPath(Guid agentId) => $"{UsagePath}?{UsageAgentKey}={agentId}";

    /// <summary>
    /// The path of the page of the notifications of a user: what the service told them, which
    /// they mark as read there (FR-NTF-1). Notifications says where it is, because an email
    /// links to it.
    /// </summary>
    public const string NotificationsPath = NotificationPaths.List;

    /// <summary>The name of the choice of a page in the address of the page of the notifications.</summary>
    public const string NotificationsPageKey = "page";

    /// <summary>The path of the page of the notifications, with a page of them.</summary>
    public static string NotificationsPagePath(int page) =>
        page > 1 ? $"{NotificationsPath}?{NotificationsPageKey}={page.ToString(CultureInfo.InvariantCulture)}" : NotificationsPath;

    /// <summary>
    /// The path of the page a user opts in to email for critical events on, and out of it
    /// (FR-NTF-3, FR-UI-2).
    /// </summary>
    public const string NotificationEmailPath = NotificationPaths.Email;

    /// <summary>The path of the page that lists the agents of a user (FR-AGT-1).</summary>
    public const string AgentsPath = "/agents";

    /// <summary>The path of the page a user creates an agent on.</summary>
    public const string NewAgentPath = "/agents/new";

    /// <summary>The path of the page a user changes the settings of an agent on. The agent is named by its id.</summary>
    public static string AgentPath(Guid agentId) => $"{AgentsPath}/{agentId}";

    /// <summary>The path of the page a user deletes an agent on.</summary>
    public static string DeleteAgentPath(Guid agentId) => $"{AgentPath(agentId)}/delete";

    /// <summary>
    /// The path of the page of the state of an agent, which is the screen of the agent: what
    /// state it is in and why, the controls that start, pause, and resume it, the changes of its
    /// state, and what it does, holds, and has made (FR-AGT-10, FR-RPT-2).
    /// </summary>
    public static string AgentStatePath(Guid agentId) => $"{AgentPath(agentId)}/state";

    /// <summary>The name of the choice of a page of the trades of an agent in the address of the page of its state.</summary>
    public const string AgentTradesPageKey = "trades";

    /// <summary>The path of the page of the state of an agent, with a page of its closed trades (FR-RPT-2).</summary>
    public static string AgentStatePath(Guid agentId, int tradesPage) =>
        tradesPage > 1
            ? $"{AgentStatePath(agentId)}?{AgentTradesPageKey}={tradesPage.ToString(CultureInfo.InvariantCulture)}"
            : AgentStatePath(agentId);

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

        // What keeps a page that follows up to date over its connection, which is the one push
        // channel from the application to the browser (FR-NTF-2, NFR-ARC-6): the feed of each
        // such page, and what says whether its connection is up, which there is one of for
        // each connection.
        services.AddSingleton<LiveFeeds>();
        services.AddScoped<LiveCircuit>();
        services.AddScoped<CircuitHandler>(provider => provider.GetRequiredService<LiveCircuit>());

        return services;
    }

    public static IEndpointRouteBuilder MapWebUi(this IEndpointRouteBuilder endpoints)
    {
        // Not a page: the button is on every page, and the answer is a redirect.
        endpoints.MapPost(SignOutPath, SignOutAsync);

        // No pages either: what the notice before a session ends asks before it says anything,
        // and what its button does (NFR-USA-1). Neither asks for a signed-in user by a policy:
        // a request whose session has ended is answered that nothing is left, and not sent to
        // the page to sign in on, which the script that asks could not read.
        endpoints.MapGet(SessionAuthentication.SessionLeftPath, SessionLeft);
        endpoints.MapPost(StaySignedInPath, StaySignedInAsync);

        // Not a page either: the answer is a file. A visitor is sent to sign in, as on a page.
        endpoints.MapGet(ExportPath, ExportAsync).RequireAuthorization();

        // Files as well: the trades and the decisions of the user, as CSV (FR-RPT-4). They are
        // of whoever is signed in, and of nobody that the address could name.
        endpoints.MapGet(TradesExportPath, ExportTrades).RequireAuthorization();
        endpoints.MapGet(DecisionsExportPath, ExportDecisions).RequireAuthorization();

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

    /// <summary>
    /// How long the session of the request has left. Asking is no activity: the session is
    /// told so when the request is signed in. See <see cref="SessionAuthentication.SessionLeftPath"/>.
    /// </summary>
    private static IResult SessionLeft(HttpContext request, ISessionEnd sessions) => LeftOf(request, sessions);

    /// <summary>
    /// What "Stay signed in" does: nothing but be a request of its user, which is activity, and
    /// say how long the session has left now.
    /// </summary>
    private static async Task<IResult> StaySignedInAsync(HttpContext request, IAntiforgery antiforgery, ISessionEnd sessions)
    {
        // As signing out: a signed-in request must come from a page of this application. One
        // that is not signed in has no session to keep, and is told that nothing is left.
        if (request.User.Identity is { IsAuthenticated: true } && !await antiforgery.IsRequestValidAsync(request))
        {
            return Results.BadRequest();
        }

        return LeftOf(request, sessions);
    }

    private static IResult LeftOf(HttpContext request, ISessionEnd sessions)
    {
        // It is of this moment, and of this user: nothing keeps it.
        request.Response.Headers.CacheControl = "no-store";

        // Whole seconds, rounded down, so the page never takes the session to last longer than
        // it does, and one second at the least: a session that stands is never said to have
        // ended, because a page that is told so asks for itself anew, which would be activity.
        return sessions.Of(request.User) is { } end
            ? Results.Json(new SessionLeft(Math.Max(1, (int)end.Left.TotalSeconds), SessionPages.At(end.Left, end.At, PageTimes.Of(request))))
            : Results.Json(new SessionLeft(0, null));
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

    private static IResult ExportTrades(HttpContext request, IExportedRecords records, TimeProvider clock) =>
        CsvFile(
            request,
            clock,
            "trades",
            ExportFiles.TradeColumns,
            records.TradesOfUserAsync(SignedInUser.RequiredIdOf(request), request.RequestAborted),
            ExportFiles.Cells);

    private static IResult ExportDecisions(HttpContext request, IExportedRecords records, TimeProvider clock) =>
        CsvFile(
            request,
            clock,
            "decisions",
            ExportFiles.DecisionColumns,
            records.DecisionsOfUserAsync(SignedInUser.RequiredIdOf(request), request.RequestAborted),
            ExportFiles.Cells);

    /// <summary>
    /// The rows of a user as a CSV file to keep, named after what it holds and the day it is of.
    /// It is written as the rows are read, and is never held as a whole: a user may have more
    /// decisions than that.
    /// </summary>
    private static IResult CsvFile<TRow>(
        HttpContext request,
        TimeProvider clock,
        string name,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<TRow> rows,
        Func<TRow, IReadOnlyList<string>> cells)
    {
        // As what is stored about a person is: for that person, and kept by nothing on the way.
        request.Response.Headers.CacheControl = "no-store";

        return Results.Stream(
            body => ExportFiles.WriteAsync(body, columns, rows, cells, request.RequestAborted),
            ExportFiles.ContentType,
            $"{name}-{clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
    }
}
