using System.Text.RegularExpressions;
using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Npgsql;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>A row of the <c>prompt_templates</c> table, read by a test directly.</summary>
internal sealed record StoredTemplate(int Id, string Kind, string Subject, int Version, string Text, bool IsActive)
{
    /// <summary>Every version of every template, in the order of their ids.</summary>
    public static IReadOnlyList<StoredTemplate> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, kind, subject, version, text, is_active FROM prompt_templates ORDER BY id", connection);
        using var reader = command.ExecuteReader();

        var templates = new List<StoredTemplate>();
        while (reader.Read())
        {
            templates.Add(new StoredTemplate(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5)));
        }

        return templates;
    }

    /// <summary>The version of a kind and subject that is in use.</summary>
    public static StoredTemplate InUse(TestDatabase database, PromptTemplateKind kind, string subject) =>
        All(database).Single(template => template.Kind == kind.ToString() && template.Subject == subject && template.IsActive);

    /// <summary>
    /// Gives a template its next version, as a migration does that was generated from the seed:
    /// the version in use is taken out of use, and the next one is stored in its place. Nothing
    /// of the earlier version is touched but whether it is in use.
    /// </summary>
    /// <returns>The version that is in use from here on.</returns>
    public static StoredTemplate GiveNextVersion(TestDatabase database, PromptTemplateKind kind, string subject, string text)
    {
        var earlier = InUse(database, kind, subject);
        var next = new StoredTemplate(
            All(database).Max(template => template.Id) + 1, earlier.Kind, earlier.Subject, earlier.Version + 1, text, IsActive: true);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var retire = new NpgsqlCommand("UPDATE prompt_templates SET is_active = false WHERE id = @id", connection, transaction))
        {
            retire.Parameters.AddWithValue("id", earlier.Id);
            retire.ExecuteNonQuery();
        }

        using (var add = new NpgsqlCommand(
            "INSERT INTO prompt_templates (id, kind, subject, version, text, is_active) VALUES (@id, @kind, @subject, @version, @text, true)",
            connection,
            transaction))
        {
            add.Parameters.AddWithValue("id", next.Id);
            add.Parameters.AddWithValue("kind", next.Kind);
            add.Parameters.AddWithValue("subject", next.Subject);
            add.Parameters.AddWithValue("version", next.Version);
            add.Parameters.AddWithValue("text", next.Text);
            add.ExecuteNonQuery();
        }

        transaction.Commit();

        return next;
    }
}

/// <summary>What a browser reads on the page of the prompts of an agent.</summary>
internal static partial class PromptBrowser
{
    /// <summary>A prompt as the page shows it: what it is for, its version, and its text.</summary>
    internal sealed record Shown(string Name, string Version, string Text);

    /// <summary>The prompts on a page, in the order the page has them, as a person reads them.</summary>
    public static IReadOnlyList<Shown> PromptsOn(string page) =>
    [
        .. Prompt().Matches(page).Select(prompt => new Shown(
            AgentBrowser.Read(prompt.Groups["name"].Value),
            AgentBrowser.Read(prompt.Groups["version"].Value),
            AgentBrowser.Read(prompt.Groups["text"].Value))),
    ];

    /// <summary>A stored template as the page shows it under a name.</summary>
    public static Shown AsShown(string name, StoredTemplate template) => new(name, $"Version {template.Version}", template.Text);

    [GeneratedRegex(
        """<h3>(?<name>[^<]*)</h3>\s*<p class="field-hint">(?<version>[^<]*)</p>\s*<p class="prompt-text">(?<text>[^<]*)</p>""")]
    private static partial Regex Prompt();
}
