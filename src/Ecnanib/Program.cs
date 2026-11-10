using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Api;
using Ecnanib.Catalog;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Experience;
using Ecnanib.Notifications;
using Ecnanib.RiskGuard;
using Ecnanib.WebUi;

/// <summary>
/// The composition root. Ecnanib is one process: every component registers here, and scheduled
/// and background work joins this same host as hosted services. The rules are in
/// docs/architecture.md.
/// </summary>
/// <remarks>
/// This is a class with a Main method, not top-level statements, because the architecture tests
/// cannot see into the method that top-level statements compile to.
/// </remarks>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Components, in dependency order, leaves first.
        builder.Services
            .AddAccounts()
            .AddAiProviderGateway()
            .AddExchangeGateway()
            .AddRiskGuard()
            .AddNotifications()
            .AddCatalog()
            .AddExperience()
            .AddCredentials()
            .AddAgents()
            .AddDecisionEngine()
            .AddWebUi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
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
        app.MapApi();
        app.MapWebUi();

        app.Run();
    }
}
