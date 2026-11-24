using System.Text.RegularExpressions;
using Ecnanib.Shared;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// The database says that a row of a user changed with a trigger of the table, which is no part
/// of the model: nothing generates it, and nothing misses it when it goes. These tests hold the
/// database, as the migrations leave it, to <see cref="LiveRules"/>. See docs/live-updates.md.
/// </summary>
public partial class LiveRuleTests
{
    [Fact]
    public void The_database_has_the_trigger_of_every_table_that_is_listed_and_of_no_other()
    {
        var found = Triggers();

        var missing = LiveRules.Tables.Keys.Except(found.Keys).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"{nameof(LiveRules)} lists tables that have no trigger which says that a row changed, so a page that follows them " +
            $"would show nothing new: {string.Join(", ", missing)}. Add the trigger with a migration that is written by hand.");

        var unlisted = found.Keys.Except(LiveRules.Tables.Keys).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            unlisted.Count == 0,
            $"These tables say that a row changed, and {nameof(LiveRules)} does not list them: {string.Join(", ", unlisted)}. " +
            "List each with what it says, and which page shows it.");

        foreach (var (table, listed) in LiveRules.Tables)
        {
            var trigger = found[table];

            Assert.True(listed.Events == trigger.Events, $"The trigger of {table} fires on {trigger.Events}, and is listed with {listed.Events}.");
            Assert.True(
                listed.Subject.ToString() == trigger.Subject,
                $"The trigger of {table} says {trigger.Subject}, and is listed with {listed.Subject}.");
            Assert.True(
                (listed.AgentColumn ?? string.Empty) == trigger.AgentColumn,
                $"The trigger of {table} reads the agent from '{trigger.AgentColumn}', and is listed with '{listed.AgentColumn}'.");
            Assert.True(
                listed.Routine.Order(StringComparer.Ordinal).SequenceEqual(trigger.Routine.Order(StringComparer.Ordinal)),
                $"The trigger of {table} says nothing of a change to {string.Join(", ", trigger.Routine)} alone, and is listed with " +
                $"{string.Join(", ", listed.Routine)}.");
        }
    }

    [Fact]
    public void Every_table_that_says_a_change_holds_rows_of_users_and_has_the_columns_that_its_trigger_names()
    {
        foreach (var (table, listed) in LiveRules.Tables)
        {
            var columns = PostgresServer.SharedDatabase.Column(
                $"SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'");

            // A signal is handed to what follows the owner of the row, which the row says itself.
            Assert.True(columns.Contains("user_id"), $"The rows of {table} say no owner, so a change of one could be told to nobody.");

            foreach (var named in listed.Routine.Concat(listed.AgentColumn is { } agent ? [agent] : []))
            {
                Assert.True(
                    columns.Contains(named),
                    $"The trigger of {table} names the column {named}, which the table does not have. A column that was renamed " +
                    "is one that the trigger no longer reads.");
            }
        }
    }

    [Fact]
    public void What_the_database_says_is_a_subject_that_a_page_can_follow_and_each_table_says_which_page()
    {
        // These two are the application's own: the step of a cycle that runs is in no table,
        // and that something was missed is said by what listens.
        LiveSubject[] ofTheApplication = [LiveSubject.CycleStep, LiveSubject.Missed];

        Assert.All(LiveRules.Tables.Values, table => Assert.DoesNotContain(table.Subject, ofTheApplication));
        Assert.All(LiveRules.Tables.Values, table => Assert.False(string.IsNullOrWhiteSpace(table.Why)));

        // Every other subject is said by a table: one that nothing says would be followed in vain.
        Assert.Equal(
            Enum.GetValues<LiveSubject>().Except(ofTheApplication).Order(),
            LiveRules.Tables.Values.Select(table => table.Subject).Distinct().Order());
    }

    /// <summary>The triggers of the database that say a change, by their table.</summary>
    private static Dictionary<string, FoundTrigger> Triggers()
    {
        var definitions = PostgresServer.SharedDatabase.Column(
            """
            SELECT pg_get_triggerdef(found.oid)
            FROM pg_trigger found
            JOIN pg_proc called ON called.oid = found.tgfoid
            WHERE NOT found.tgisinternal AND called.proname = 'live_signal'
            """);

        return definitions
            .Select(definition =>
            {
                var read = Definition().Match(definition);
                Assert.True(read.Success, $"A trigger that says a change does not read as one of a row, after the change: {definition}");

                var arguments = Argument().Matches(read.Groups["arguments"].Value).Select(argument => argument.Groups[1].Value).ToList();
                Assert.NotEmpty(arguments);

                return (
                    Table: read.Groups["table"].Value,
                    Trigger: new FoundTrigger(
                        read.Groups["events"].Value,
                        arguments[0],
                        arguments.Count > 1 ? arguments[1] : string.Empty,
                        [.. arguments.Skip(2)]));
            })
            .ToDictionary(found => found.Table, found => found.Trigger);
    }

    /// <summary>A trigger as the database has it.</summary>
    private sealed record FoundTrigger(string Events, string Subject, string AgentColumn, IReadOnlyList<string> Routine);

    [GeneratedRegex(@"AFTER (?<events>.+) ON (?:public\.)?(?<table>\w+) FOR EACH ROW EXECUTE FUNCTION live_signal\((?<arguments>.*)\)$")]
    private static partial Regex Definition();

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex Argument();
}
