using Ecnanib.Shared;

namespace Ecnanib.Platform.LiveUpdates;

/// <summary>
/// The signals that tell the open pages of a user that something of theirs changed, so that a
/// page shows it without a manual refresh (FR-NTF-2). See docs/live-updates.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A signal is of one user,</b> and is handed to what follows that user, and to nothing else.
/// The user of a signal is the owner of the row that changed, which the database says, or the
/// owner that whoever raises it names.
/// </para>
/// <para>
/// <b>A signal holds nothing of what changed.</b> What follows asks for it, by owner, as a page
/// asks. So a signal that reached the wrong follower would show nobody anything.
/// </para>
/// <para>
/// <b>The database says what changed in it,</b> whatever wrote it: its triggers say a change
/// when it is committed, and not before, and one listener of each running instance hears them.
/// What the database does not hold is raised here.
/// </para>
/// </remarks>
public interface ILiveSignals
{
    /// <summary>
    /// How many signals this instance has heard so far. A page asks before it reads what it
    /// shows, and what follows for the page says the number to <see cref="Follow"/>: a signal
    /// that was heard after it is one that the page may not show yet. Asking also has this
    /// instance listen to the database, when it does not yet.
    /// </summary>
    long Mark();

    /// <summary>Follows the signals of one user, until what it returns is disposed of.</summary>
    /// <param name="userId">The user whose signals are followed.</param>
    /// <param name="since">
    /// What <see cref="Mark"/> said before what follows last read what it shows. The signals of
    /// the user that were heard since are told at once, so that nothing is missed between the
    /// read and the follow. Where that cannot be said any more, because it is too long ago, or
    /// because this instance was not listening all the while, <see cref="LiveSubject.Missed"/>
    /// is told instead.
    /// </param>
    /// <param name="heard">
    /// What is told each signal. It is called by whoever raised the signal, so it returns at
    /// once, and what it throws is logged and reaches nobody.
    /// </param>
    IDisposable Follow(Guid userId, long since, Action<LiveSignal> heard);

    /// <summary>
    /// Says that something of a user changed that the database does not hold, such as the step
    /// of a cycle that runs. It reaches what follows the user on this instance, and never throws.
    /// </summary>
    /// <param name="userId">The user whose it is.</param>
    /// <param name="agentId">The agent that it is about, or null when it is about none.</param>
    /// <param name="subject">What changed.</param>
    void Raise(Guid userId, Guid? agentId, LiveSubject subject);
}

/// <summary>A signal as what follows a user hears it.</summary>
/// <param name="Subject">What changed.</param>
/// <param name="AgentId">The agent that it is about, or null when it is about none, or about all that the user has.</param>
public readonly record struct LiveSignal(LiveSubject Subject, Guid? AgentId);
