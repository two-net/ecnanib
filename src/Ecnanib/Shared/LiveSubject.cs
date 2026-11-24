namespace Ecnanib.Shared;

/// <summary>
/// What a signal to the open pages of a user is about: the kind of thing of theirs that changed
/// (FR-NTF-2). A signal says this, whose it is, and which agent it is about, and nothing of what
/// changed: that is asked for. See docs/live-updates.md.
/// </summary>
/// <remarks>
/// The database names a subject by the name of its member, in the triggers that say a change, so
/// renaming a member needs a migration.
/// </remarks>
public enum LiveSubject
{
    /// <summary>An agent: it was created or deleted, or its state, its mode, or a setting of it changed.</summary>
    Agent,

    /// <summary>What an agent holds: a position or an order of it was written.</summary>
    Holdings,

    /// <summary>A cycle of an agent was recorded.</summary>
    Cycle,

    /// <summary>
    /// The equity of an agent was recorded: what it made or lost has changed, or what its open
    /// position is worth has. The screen of the agent shows it over time (FR-RPT-2).
    /// </summary>
    Equity,

    /// <summary>A cycle of an agent that runs started, moved on to its next step, or ended.</summary>
    CycleStep,

    /// <summary>A notification of the user was raised, read, or removed.</summary>
    Notification,

    /// <summary>A session of the user ended.</summary>
    Session,

    /// <summary>
    /// Something may have changed while nothing was heard. Whoever follows asks for everything
    /// that it shows again.
    /// </summary>
    Missed,
}
