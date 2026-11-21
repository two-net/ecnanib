using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The prompt templates that are in use, as whatever composes a prompt takes them and as a user
/// reads them (FR-PRM-7). See docs/prompts.md.
/// </summary>
/// <remarks>
/// It only reads. Templates are managed by the system and arrive through migrations
/// (NFR-DAT-10): nothing here stores, changes, or deletes one, and the database refuses a change
/// to a version whoever asks for it (FR-PRM-6). The templates are the same for every user: their
/// rows belong to the service, and name no user. Whose agent a question is about is for whoever
/// asks to settle first.
/// </remarks>
public interface IPromptTemplates
{
    /// <summary>
    /// The templates that an agent on a market and a timeframe uses now: the one of each role,
    /// the module of the market, the module of the timeframe, the module of the profile, and the
    /// instructions for the lesson of a closed position. The market and the timeframe are the
    /// agent's own settings, so a module that does not go with them cannot be asked for (FR-PRM-4).
    /// </summary>
    /// <param name="market">The market of the agent.</param>
    /// <param name="timeframe">The timeframe of the agent.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<TemplatesInUse> InUseAsync(Market market, Timeframe timeframe, CancellationToken cancellationToken);
}

/// <summary>
/// The templates that an agent uses, each in the version that is in use. A template says its
/// version and its id, which is what a role output records of it (FR-PRM-6).
/// </summary>
/// <param name="Roles">The instructions of each of the four roles.</param>
/// <param name="Market">The module of the agent's market.</param>
/// <param name="Timeframe">The module of the agent's timeframe.</param>
/// <param name="Profile">The module of the trading profile, which is the same for every agent.</param>
/// <param name="Lesson">
/// The instructions for the lesson that the model of the agent's Risk Manager writes after a
/// closed position (FR-EXP-6), which are the same for every agent. No call of a role uses them.
/// </param>
public sealed record TemplatesInUse(
    IReadOnlyDictionary<Role, PromptTemplate> Roles,
    PromptTemplate Market,
    PromptTemplate Timeframe,
    PromptTemplate Profile,
    PromptTemplate Lesson)
{
    /// <summary>
    /// The templates that one call of a role uses, in the order a prompt is composed in: the
    /// instructions of the role, then the modules of the market, the timeframe, and the profile
    /// (FR-PRM-1). These are the template versions that the output of the call records.
    /// </summary>
    /// <param name="role">The role that is called.</param>
    public IReadOnlyList<PromptTemplate> For(Role role) => [Roles[role], Market, Timeframe, Profile];
}
