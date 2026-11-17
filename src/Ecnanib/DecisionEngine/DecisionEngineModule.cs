using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Decision Engine: schedules cycles, composes prompts, runs the team, validates outputs (SRS 3.1).
/// See docs/cycles.md and docs/prompts.md for what of it is built.
/// </summary>
public static class DecisionEngineModule
{
    /// <summary>
    /// Registers the component: what starts a cycle for each running agent when a candle closes
    /// (FR-CYC-1), what builds the data package of a cycle (FR-CYC-2), what reads the prompt
    /// templates that are in use (FR-PRM-7), what composes the prompt of a role call from them
    /// (FR-PRM-1), and what says whether the model of a role searches live for its call
    /// (FR-CYC-3).
    /// </summary>
    public static IServiceCollection AddDecisionEngine(this IServiceCollection services)
    {
        // What a cycle does once it has started: it builds its data package, and makes no
        // decision, for the calls of the team are not built. What reconciles an agent with the
        // exchange registers an IReconciliation, when it is built.
        services.AddSingleton<IDecisionCycle, NoDecisionYet>();

        // The data package of a cycle: candles, the context of the higher timeframes, the
        // indicators, which the system computes, and what the agent holds. What keeps positions
        // and orders registers an IAgentHoldings, when it is built: until then an agent holds
        // nothing, and its equity is its allocated capital.
        services.AddSingleton<IDataPackages, DataPackages>();

        // The candles that a cycle starts with, asked for until they reach its close (FR-CYC-1).
        services.AddSingleton<ClosedCandles>();

        // Starts the cycles at every quarter hour, which the scheduler of the platform runs: it
        // skips the closes that passed while the application was down (FR-CYC-8), and the job
        // queues the cycles of a close with the limits of the configuration file (FR-CYC-11).
        services.AddBackgroundJob<DecisionCyclesJob>();

        // The templates as a prompt takes them and as a user reads them. It only reads: a
        // template arrives with a migration (NFR-DAT-10).
        services.AddSingleton<IPromptTemplates, PromptTemplates>();

        // What a prompt never holds: a secret, an email address, or an account identifier
        // (FR-PRM-9). It knows the secrets of the configuration file from the mask of the log.
        services.AddSingleton<PromptScreen>();

        // The prompt of a role call, from the templates of the agent's market and timeframe and
        // from what the call is given. Every part of it passes the screen. It calls no model.
        services.AddSingleton<IPromptComposition, PromptComposition>();

        // What the model of a role is called with. Only the Sentiment Analyst searches live, and
        // only with a model that the configuration file says can (FR-CYC-3). It makes no call:
        // what runs the roles of a cycle is not built.
        services.AddSingleton<IRoleCalls, RoleCalls>();

        return services;
    }
}
