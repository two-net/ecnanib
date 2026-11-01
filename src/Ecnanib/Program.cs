using Ecnanib.Components;
using Ecnanib.Modules.Administration;
using Ecnanib.Modules.Agents;
using Ecnanib.Modules.AiAdapters;
using Ecnanib.Modules.AiCouncil;
using Ecnanib.Modules.Catalog;
using Ecnanib.Modules.Credentials;
using Ecnanib.Modules.DecisionCycle;
using Ecnanib.Modules.ExchangeAdapter;
using Ecnanib.Modules.Execution;
using Ecnanib.Modules.Experience;
using Ecnanib.Modules.Identity;
using Ecnanib.Modules.MarketData;
using Ecnanib.Modules.Notifications;
using Ecnanib.Modules.RiskEngine;
using Ecnanib.Modules.Scheduler;
using Ecnanib.Platform;

var builder = WebApplication.CreateBuilder(args);

// One assembly gives no compile-time protection against DI cycles, and the default host only
// validates in Development, so a bad registration must fail at startup in every environment.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Modules, lowest level first. See docs/architecture.md.
builder.Services
    .AddPlatform()
    .AddRiskEngine()
    .AddExchangeAdapter()
    .AddAiAdapters()
    .AddIdentity()
    .AddNotifications()
    .AddCredentials()
    .AddCatalog()
    .AddMarketData()
    .AddAgents()
    .AddExecution()
    .AddAiCouncil()
    .AddDecisionCycle()
    .AddExperience()
    .AddScheduler()
    .AddAdministration();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.RunWithMaskedFailure();
