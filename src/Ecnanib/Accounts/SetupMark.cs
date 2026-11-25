namespace Ecnanib.Accounts;

/// <summary>
/// What a user says of themself on the setup checklist of a first-time user (FR-UI-4): the two
/// steps of it that nothing stored can tell. That keys are stored and that an agent was created
/// is read from the keys and the agents. That its user reviewed decisions, and considered Live,
/// is known from the user alone. See docs/dashboard.md.
/// </summary>
public enum SetupMark
{
    /// <summary>The user reviewed what a team of theirs decided.</summary>
    DecisionsReviewed,

    /// <summary>
    /// The user considered Live: they said so, or they switched an agent to Live, which is to
    /// have considered it.
    /// </summary>
    LiveConsidered,
}
