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
using Ecnanib.Platform;
using Ecnanib.Platform.Settings;
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
    public static int Main(string[] args) => Run(args, Console.Error);

    /// <summary>
    /// Runs the application until it is stopped. When the configuration file is missing or
    /// invalid it does not start: it writes which keys are wrong to <paramref name="errors"/>,
    /// without their values, and returns a non-zero exit code (NFR-CFG-4).
    /// </summary>
    public static int Run(string[] args, TextWriter errors)
    {
        // Settings come from the one configuration file and nowhere else. See docs/configuration.md.
        WebApplicationBuilder builder;
        try
        {
            builder = PlatformModule.CreateBuilder(args);
        }
        catch (InvalidSettingsException invalid)
        {
            errors.WriteLine("Ecnanib cannot start.");
            errors.WriteLine(invalid.Message);
            return 1;
        }

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

        return 0;
    }
}
