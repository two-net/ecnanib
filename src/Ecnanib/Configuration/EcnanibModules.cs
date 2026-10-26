using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Ai;
using Ecnanib.Council;
using Ecnanib.Database;
using Ecnanib.Exchange;
using Ecnanib.Execution;
using Ecnanib.Experience;
using Ecnanib.Health;
using Ecnanib.Keys;
using Ecnanib.Logging;
using Ecnanib.MarketData;
using Ecnanib.ModelSync;
using Ecnanib.Strategies;
using Ecnanib.Ui;

namespace Ecnanib.Configuration;

/// <summary>
/// Every module as the application registers it, in the order their defaults and replacements require. <c>Program.cs</c>
/// adds the log sinks before it (C-08) and the Razor components; the tests' in-memory server registers the same set, so
/// what is tested is what runs. Each module binds and validates its own section of the configuration file (FR-CFG-06).
/// </summary>
public static class EcnanibModules
{
    public static IServiceCollection AddEcnanibModules(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddServer(configuration);
        // Before AddAccounts: visitors and new accounts take the Ui section's theme and animation defaults (Appendix D, FR-UI-02).
        services.AddUi(configuration);
        services.AddExchange(configuration);
        services.AddAiProviders(configuration);
        services.AddDatabase(configuration);
        services.AddAccounts(configuration);
        services.AddModelSync(configuration);
        // Before AddAgents: the Appendix A catalog and engine the agents module requires (FR-STR).
        services.AddStrategies();
        // Before AddCouncil and AddAgents: the experience summaries, the shadow-outcome tracker and the council suggestions
        // replace the council's and the agents module's defaults (FR-EXP).
        services.AddExperience(configuration);
        // Before AddAgents: the Appendix B council replaces the agents module's fail-safe default (FR-AI).
        services.AddCouncil(configuration);
        // Before AddAgents: the symbol catalog and the backfilled, streamed market data replace the agents module's defaults (FR-DATA).
        services.AddMarketData(configuration);
        // Before AddAgents: sizing, the live venue, fills and reconciliation replace the agents module's fail-safe execution (FR-EXE).
        services.AddExecution(configuration);
        // Before AddKeys: the runtime's own key handling (stop with orders cancelled, Error halts evaluation) replaces the default.
        services.AddAgents(configuration);
        services.AddKeys(configuration);
        // After AddAgents and AddModelSync: the health report reads the scheduler, the exchange streams, the model sync states and the database (NFR-OBS-01).
        services.AddHealth();
        // The log viewer reads log_entries scoped to the user's own agents (FR-LOG-07); AddLogSinks fills the table.
        services.AddLogReader();

        return services;
    }
}
