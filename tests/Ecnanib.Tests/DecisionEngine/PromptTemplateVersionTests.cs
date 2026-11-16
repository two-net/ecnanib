using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Prompt templates are versioned. A version that has been used is never changed, and an edit
/// creates a new version (FR-PRM-6). The database holds to it on its own, in a database that has
/// had the migrations applied and nothing else done to it: whatever asks for a change to a
/// version is refused, a migration among them.
/// </summary>
public class PromptTemplateVersionTests
{
    private static readonly Lazy<TestDatabase> Migrated = new(() =>
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        return database;
    });

    [Theory]
    [InlineData("text = 'Rewritten'")]
    [InlineData("text = text || ' And more.'")]
    [InlineData("version = 2")]
    [InlineData("subject = 'Margin'")]
    [InlineData("kind = 'Role'")]
    [InlineData("id = 500")]
    [InlineData("text = 'Rewritten', is_active = false")]
    public void A_version_of_a_template_is_never_changed(string change)
    {
        // Nothing here changes a row, so the tests of this class read one database.
        var database = Migrated.Value;
        var before = StoredTemplate.All(database);

        var refused = Assert.Throws<PostgresException>(() =>
            database.Execute($"UPDATE prompt_templates SET {change} WHERE kind = 'Market' AND subject = 'Spot'"));

        // Refused by the rule of the versions, which says what to do instead, and not by an
        // index that the change happened to meet.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        Assert.Contains("never changed", refused.MessageText);
        Assert.Contains("add its next version (FR-PRM-6)", refused.MessageText);
        Assert.Equal(before, StoredTemplate.All(database));
    }

    [Theory]
    [InlineData("DELETE FROM prompt_templates WHERE kind = 'Market' AND subject = 'Spot'")]
    [InlineData("DELETE FROM prompt_templates")]
    [InlineData("TRUNCATE prompt_templates")]
    public void A_version_of_a_template_is_never_deleted(string deletion)
    {
        var database = Migrated.Value;
        var before = StoredTemplate.All(database);

        var refused = Assert.Throws<PostgresException>(() => database.Execute(deletion));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        Assert.Contains("(FR-PRM-6)", refused.MessageText);
        Assert.Equal(before, StoredTemplate.All(database));
    }

    [Fact]
    public void An_edit_is_a_new_version_and_the_earlier_one_stays_as_it_was()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();

        var first = StoredTemplate.InUse(database, PromptTemplateKind.Market, "Spot");
        var others = StoredTemplate.All(database).Where(template => template.Id != first.Id).ToList();

        // The edit: the next version is stored, and takes the place of the earlier one.
        var second = StoredTemplate.GiveNextVersion(database, PromptTemplateKind.Market, "Spot", "Long-only, said anew.");

        Assert.Equal(first.Version + 1, second.Version);
        Assert.Equal(second, StoredTemplate.InUse(database, PromptTemplateKind.Market, "Spot"));

        // The earlier one is stored as it was used: nothing of it changed but that it is no
        // longer in use. And no other template was touched.
        Assert.Equal(
            others.Append(first with { IsActive = false }).Append(second).OrderBy(template => template.Id),
            StoredTemplate.All(database));

        // A version that is out of use is held to the same for good: it is what was used.
        foreach (var change in new[] { $"UPDATE prompt_templates SET text = 'Rewritten' WHERE id = {first.Id}", $"DELETE FROM prompt_templates WHERE id = {first.Id}" })
        {
            var refused = Assert.Throws<PostgresException>(() => database.Execute(change));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        }

        // And so is the one that is in use now.
        Assert.Equal(
            PostgresErrorCodes.RestrictViolation,
            Assert.Throws<PostgresException>(() =>
                database.Execute($"UPDATE prompt_templates SET text = 'Rewritten' WHERE id = {second.Id}")).SqlState);
    }

    [Fact]
    public void The_seed_names_every_version_and_the_latest_of_each_is_the_one_in_use()
    {
        using var context = ModelOnly.Context();

        // The rows that are seeded are in the model as it is designed, and not in the one that
        // the application reads at run time.
        var seeded = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(PromptTemplate))!.GetSeedData()
            .Select(row => new StoredTemplate(
                (int)row[nameof(PromptTemplate.Id)]!,
                ((PromptTemplateKind)row[nameof(PromptTemplate.Kind)]!).ToString(),
                (string)row[nameof(PromptTemplate.Subject)]!,
                (int)row[nameof(PromptTemplate.Version)]!,
                (string)row[nameof(PromptTemplate.Text)]!,
                (bool)row[nameof(PromptTemplate.IsActive)]!))
            .ToList();

        Assert.NotEmpty(seeded);
        Assert.Equal(seeded.Count, seeded.Select(template => template.Id).Distinct().Count());
        Assert.All(seeded, template => Assert.False(string.IsNullOrWhiteSpace(template.Text), $"Template {template.Id} has no text."));

        // A template is of one of the four kinds (FR-PRM-1).
        Assert.Equal(["Role", "Market", "Timeframe", "Profile"], Enum.GetNames<PromptTemplateKind>());

        // The versions of a kind and subject count up from 1, with none left out, and the latest
        // is the one in use: an edit adds a version, and takes away none.
        Assert.All(seeded.GroupBy(template => (template.Kind, template.Subject)), versions =>
        {
            var inOrder = versions.OrderBy(template => template.Version).ToList();

            Assert.Equal(Enumerable.Range(1, inOrder.Count), inOrder.Select(template => template.Version));
            Assert.Equal(
                [.. Enumerable.Repeat(false, inOrder.Count - 1), true],
                inOrder.Select(template => template.IsActive));
        });
    }
}
