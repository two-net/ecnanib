namespace Ecnanib.DecisionEngine;

/// <summary>
/// Decision Engine: schedules cycles, composes prompts, runs the team, validates outputs (SRS 3.1).
/// See docs/prompts.md for what of it is built.
/// </summary>
public static class DecisionEngineModule
{
    /// <summary>
    /// Registers the component: what reads the prompt templates that are in use (FR-PRM-7), and
    /// what composes the prompt of a role call from them (FR-PRM-1).
    /// </summary>
    public static IServiceCollection AddDecisionEngine(this IServiceCollection services)
    {
        // The templates as a prompt takes them and as a user reads them. It only reads: a
        // template arrives with a migration (NFR-DAT-10).
        services.AddSingleton<IPromptTemplates, PromptTemplates>();

        // The prompt of a role call, from the templates of the agent's market and timeframe and
        // from what the call is given. It calls no model.
        services.AddSingleton<IPromptComposition, PromptComposition>();

        return services;
    }
}
