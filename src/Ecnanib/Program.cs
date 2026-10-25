using System.Net.Sockets;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Ai;
using Ecnanib.Components;
using Ecnanib.Configuration;
using Ecnanib.Council;
using Ecnanib.Database;
using Ecnanib.Database.Migrations;
using Ecnanib.Exchange;
using Ecnanib.Execution;
using Ecnanib.Experience;
using Ecnanib.Keys;
using Ecnanib.MarketData;
using Ecnanib.ModelSync;
using Ecnanib.Strategies;
using Ecnanib.Ui;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

// FR-CFG-01/02/06: the one configuration file, from --config or ./ecnanib.json, is the only source. A bad file ends
// here with its reason on the console and exit code 1; no logger exists yet, so the message goes straight to stderr.
WebApplicationBuilder builder;
try
{
    builder = EcnanibHost.CreateBuilder(ConfigurationFile.ResolvePath(args));
}
catch (ConfigurationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddServer(builder.Configuration);
// Before AddAccounts: visitors and new accounts take the Ui section's theme and animation defaults (Appendix D, FR-UI-02).
builder.Services.AddUi(builder.Configuration);
builder.Services.AddExchange(builder.Configuration);
builder.Services.AddAiProviders(builder.Configuration);
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddAccounts(builder.Configuration);
builder.Services.AddModelSync(builder.Configuration);
// Before AddAgents: the Appendix A catalog and engine the agents module requires (FR-STR).
builder.Services.AddStrategies();
// Before AddCouncil and AddAgents: the experience summaries, the shadow-outcome tracker and the council suggestions
// replace the council's and the agents module's defaults (FR-EXP).
builder.Services.AddExperience(builder.Configuration);
// Before AddAgents: the Appendix B council replaces the agents module's fail-safe default (FR-AI).
builder.Services.AddCouncil(builder.Configuration);
// Before AddAgents: the symbol catalog and the backfilled, streamed market data replace the agents module's defaults (FR-DATA).
builder.Services.AddMarketData(builder.Configuration);
// Before AddAgents: sizing, the live venue, fills and reconciliation replace the agents module's fail-safe execution (FR-EXE).
builder.Services.AddExecution(builder.Configuration);
// Before AddKeys: the runtime's own key handling (stop with orders cancelled, Error halts evaluation) replaces the default.
builder.Services.AddAgents(builder.Configuration);
builder.Services.AddKeys(builder.Configuration);

await using var app = builder.Build();

try
{
    // DB-02, NFR-ARCH-03: bring the schema up to date before anything else runs; every configured section is
    // validated first (FR-CFG-06). The cause was logged at Critical, so the process exits without a stack trace.
    await app.MigrateDatabaseAsync();
    // FR-CFG-05: the key ring that signs sessions exists after the first start, not after the first request.
    app.EnsureSystemKeys();
}
catch (Exception ex) when (ex is OptionsValidationException or DatabaseMigrationException
                           || ex is AggregateException aggregate && aggregate.InnerExceptions.All(e => e is OptionsValidationException))
{
    return 1;
}

// There is no Development environment: the error page and status-code pages are the same everywhere.
app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// NFR-SEC-01: antiforgery tokens are bound to the signed-in identity, so authentication runs first. The explicit calls
// fix the order instead of leaving it to WebApplication's automatic insertion.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccountEndpoints();

// Resolved before RunAsync, which disposes the host on its way out, including when the server fails to start.
var endPoint = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.ToEndPoint();
try
{
    await app.RunAsync();
}
catch (Exception ex) when (ex is IOException { InnerException: AddressInUseException } or SocketException)
{
    // The Server section names an endpoint this host cannot bind; the same clear exit as any other invalid setting.
    app.Logger.LogCritical("Cannot listen on {EndPoint}; the application will not start: {Reason}", endPoint, ex.Message);
    return 1;
}

return 0;
