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
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.RiskGuard;
using Ecnanib.WebUi;

/// <summary>
/// The composition root. Ecnanib is one process: every component registers here, and the jobs
/// a component registers run in this same host, on the platform's scheduler. The rules are in
/// docs/architecture.md and docs/background-work.md.
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
    /// without their values, and returns a non-zero exit code (NFR-CFG-4). It also returns a
    /// non-zero exit code when an error stops the application, after logging it (NFR-LOG-6). A
    /// database that cannot be migrated is such an error: the application does not start (NFR-DAT-3).
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
            .AddWebUi()
            .AddApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline. Correlation comes first, so that everything after
        // it, error handling included, logs with the request's properties.
        app.UseRequestCorrelation();

        if (!app.Environment.IsDevelopment())
        {
            // An unhandled error is logged once, under a reference id, by the platform. The API
            // answers with that id as JSON, and every other request gets the error page.
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandlingPath = "/Error",
                CreateScopeForErrors = true,

                // The middleware would log the error a second time, without the reference id.
                SuppressDiagnosticsCallback = _ => true,
            });

            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapApi();
        app.MapWebUi();

        // The same as app.Run(), except that an error which stops the application is logged
        // while the log outputs are still open.
        try
        {
            // Pending migrations are applied first. Requests are served and scheduled work is
            // started only by app.Start(), so neither can meet a database that is behind.
            app.MigrateDatabase();

            app.Start();
            app.WaitForShutdown();

            return 0;
        }
        catch (Exception error)
        {
            app.Logger.LogCritical(error, "Ecnanib stopped because of an unhandled error");

            return 1;
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
