using Ecnanib.Shared;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>What the rows of a table say when they change: its trigger, as the database is to have it.</summary>
/// <param name="Subject">What a change of a row is about.</param>
/// <param name="AgentColumn">The column that says the agent, or null where the rows are about none.</param>
/// <param name="Routine">The columns whose change alone is said to nobody.</param>
/// <param name="Events">What is said: a row that is added, changed, and removed, in the words of the database.</param>
/// <param name="Why">Which page shows it.</param>
internal sealed record LiveTable(LiveSubject Subject, string? AgentColumn, IReadOnlyList<string> Routine, string Events, string Why);

/// <summary>
/// Which tables say that a row of a user changed, to the open pages of that user, in one place
/// (FR-NTF-2). This file is the source of truth: <c>docs/live-updates.md</c> explains the rules,
/// and <see cref="LiveRuleTests"/> holds the triggers of the database to them.
/// </summary>
/// <remarks>
/// The triggers are no part of the model, so nothing generates them, and a migration that
/// rebuilds a table drops its trigger without a word. A table that a page comes to follow is
/// listed here, and given its trigger by a migration that is written by hand.
/// </remarks>
internal static class LiveRules
{
    private const string Written = "INSERT OR DELETE OR UPDATE";

    public static readonly IReadOnlyDictionary<string, LiveTable> Tables = new Dictionary<string, LiveTable>
    {
        ["agents"] = new(
            LiveSubject.Agent,
            "id",
            ["roles_reassigned_at"],
            Written,
            "The dashboard, the list of the agents, and the pages of an agent: its state, its mode, and its settings. When the " +
            "service last gave a role another model is on the form of the agent alone, which follows nothing."),

        ["positions"] = new(
            LiveSubject.Holdings,
            "agent_id",
            ["alerted_at", "funding_due_at"],
            Written,
            "The position of an agent on the page of its state, and whether it has one, which the list says as well. The open " +
            "positions of the dashboard, and the prices that are followed for it."),

        ["orders"] = new(
            LiveSubject.Holdings,
            "agent_id",
            ["checked_at"],
            Written,
            "Whether an agent has an open position, which an entry that is on its way says, and the orders on the timeline of a " +
            "cycle, each as it is now."),

        ["decision_cycles"] = new(
            LiveSubject.Cycle,
            "agent_id",
            [],
            Written,
            "The list of the cycles of an agent, and the recent decisions of the dashboard."),

        ["notifications"] = new(
            LiveSubject.Notification,
            "agent_id",
            ["email_pending", "emailed_at"],
            Written,
            "The page of the notifications, those of an agent on the page of its state, the alerts of the dashboard, and the count " +
            "in the bar of a page that follows."),

        ["sessions"] = new(
            LiveSubject.Session,
            AgentColumn: null,
            [],
            "DELETE",
            "No page shows it. A page that follows is of a session, and stops when that ends. A session that is used is written to " +
            "with every minute of use, which nobody is told."),
    };
}
