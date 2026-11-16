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
    /// (FR-CYC-1), what reads the prompt templates that are in use (FR-PRM-7), and what composes
    /// the prompt of a role call from them (FR-PRM-1).
    /// </summary>
    public static IServiceCollection AddDecisionEngine(this IServiceCollection services)
    {
        // What a cycle does once it has started, which is not built: it does nothing. What
        // reconciles an agent with the exchange registers an IReconciliation, when it is built.
        services.AddSingleton<IDecisionCycle, NoDecisionYet>();

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

        return services;
    }
}
