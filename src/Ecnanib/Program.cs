using Ecnanib.Components;
using Ecnanib.Endpoints;
using Ecnanib.Endpoints.Security;
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
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddWebSecurity();

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

// Configure the HTTP request pipeline. The order matters: see Web security in docs/architecture.md.
app.UseCorrelation();
app.UseRequestLogging();
app.UseWebSecurity();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // After the exception handler, because it sets the header directly and the handler clears it.
    app.UseHsts();
}

// Before the limiter, or it can't see the account.
app.UseAccountAuthentication();
// Before status pages, so a 404 costs one permit and a 429 isn't sent through the 404 page.
app.UseRateLimiter();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStatusPagesAsGet();
app.UseHttpsRedirectionExceptHealth();

// Inside status pages, so a 403 renders one. Antiforgery tokens bind to the identity, so it comes after.
app.UseAuthorization();
app.UseAntiforgery();

// A page load fetches several assets, and they cost almost nothing to serve.
app.MapStaticAssets().AllowAnonymous().DisableRateLimiting();
app.MapHealthEndpoints();
app.MapAccountEndpoints();

// Pages are private through [Authorize] in Components/_Imports.razor. Never chain AllowAnonymous here:
// this builder's conventions reach every page, so it would make all of them public.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.RunWithMaskedFailure();
