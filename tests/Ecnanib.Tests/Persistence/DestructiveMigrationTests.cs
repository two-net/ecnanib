using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// A migration that drops or rewrites data is marked as destructive (NFR-DAT-11). These tests
/// read the migrations and need no database. That a marked migration is preceded by a backup is
/// held by <see cref="MigrationBackupTests"/>. See docs/backups.md.
/// </summary>
public class DestructiveMigrationTests
{
    [Fact]
    public void Every_migration_that_drops_or_rewrites_data_is_marked_as_destructive()
    {
        var unmarked = Migrations()
            .Where(migration => !migration.Marked && migration.Loses.Count > 0)
            .Select(migration => $"  {migration.Id}: {string.Join(", ", migration.Loses.Distinct())}")
            .ToList();

        Assert.True(
            unmarked.Count == 0,
            $"A migration that drops or rewrites data carries [{nameof(DestructiveMigrationAttribute).Replace("Attribute", "")}] on its class, " +
            "so that the database is backed up before it is applied:" + Environment.NewLine + string.Join(Environment.NewLine, unmarked));
    }

    [Fact]
    public void The_migrations_that_are_marked_are_these()
    {
        // Pinned, so that a mark that is taken away shows in a review, as one that is added does.
        // Each of them rewrites: it fills a column that it added, changes seeded rows, or both.
        Assert.Equal(
            [
                "20261111183920_AddSignIn",
                "20261116102346_AddAgentStateChangeMode",
                "20261116131025_AddAgentRiskFactors",
                "20261116161733_AddPromptModules",
                "20261116170831_AddPromptUntrustedContent",
                "20261116202826_AddPromptLiveData",
                "20261117044119_AddRoleInstructions",
                "20261117061132_AddStrategistPlanRules",
                "20261118190347_AddFundings",
                "20261119152244_AddPositionResults",
                "20261119170329_AddCycleKind",
                "20261121155752_AddLessons",
                "20261124044333_AddDashboard",
                "20261125161032_AddSetupMarks",
            ],
            Migrations().Where(migration => migration.Marked).Select(migration => migration.Id));

        // None of them is marked for something that the rule does not see.
        Assert.All(Migrations().Where(migration => migration.Marked), migration => Assert.NotEmpty(migration.Loses));
    }

    [Fact]
    public void What_drops_or_rewrites_data_is_found()
    {
        MigrationOperation[] operations =
        [
            new DropTableOperation { Name = "lessons" },
            new DropColumnOperation { Table = "users", Name = "theme" },
            new DropSchemaOperation { Name = "archive" },
            new DeleteDataOperation { Table = "roles", KeyColumns = ["code"], KeyValues = new object[,] { { "Strategist" } } },
            new UpdateDataOperation
            {
                Table = "prompt_templates", KeyColumns = ["id"], KeyValues = new object[,] { { 1 } }, Columns = ["is_active"], Values = new object[,] { { false } },
            },
            new AlterColumnOperation
            {
                Table = "orders", Name = "quantity", ClrType = typeof(decimal), ColumnType = "numeric(18,2)",
                OldColumn = new AddColumnOperation { ClrType = typeof(decimal), ColumnType = "numeric" },
            },
            new AlterColumnOperation
            {
                Table = "agents", Name = "capital", ClrType = typeof(long), OldColumn = new AddColumnOperation { ClrType = typeof(decimal) },
            },
            new SqlOperation { Sql = "UPDATE users SET theme = 'System'" },
            new SqlOperation { Sql = "\n                -- Agents of users who are gone.\n                delete from agents WHERE user_id IS NULL\n" },
            new SqlOperation { Sql = "/* All of it. */ TRUNCATE notifications" },
            new SqlOperation { Sql = "DROP  TABLE IF EXISTS lessons" },
            new SqlOperation { Sql = "DROP SCHEMA archive CASCADE" },
            new SqlOperation { Sql = "ALTER TABLE users\n    DROP COLUMN theme" },
            new SqlOperation { Sql = "ALTER TABLE orders ALTER COLUMN quantity TYPE integer USING quantity::integer" },
            new SqlOperation { Sql = "WITH gone AS (SELECT id FROM users WHERE email IS NULL) DELETE FROM agents USING gone WHERE agents.user_id = gone.id" },
            new SqlOperation { Sql = "INSERT INTO runtime_flags (name, value) VALUES ('halt', false) ON CONFLICT (name) DO UPDATE SET value = false" },
        ];

        Assert.Equal(
            [
                "drops the table lessons",
                "drops the column users.theme",
                "drops the schema archive",
                "deletes rows of roles",
                "updates rows of prompt_templates",
                "changes the type of orders.quantity",
                "changes the type of agents.capital",
                "sends a statement of UPDATE",
                "sends a statement of DELETE",
                "sends a statement of TRUNCATE",
                "sends a statement of DROP TABLE",
                "sends a statement of DROP SCHEMA",
                "sends a statement of ALTER TABLE with DROP COLUMN",
                "sends a statement of ALTER TABLE with TYPE",
                "sends a statement of WITH and UPDATE or DELETE",
                "sends a statement of INSERT with ON CONFLICT DO UPDATE",
            ],
            DestructiveRules.FindIn(operations));
    }

    [Fact]
    public void What_only_adds_is_not_found()
    {
        MigrationOperation[] operations =
        [
            new CreateTableOperation { Name = "restore_tests" },
            new AddColumnOperation { Table = "users", Name = "theme", ClrType = typeof(string) },
            new CreateIndexOperation { Table = "users", Name = "ix_users_email", Columns = ["email"] },
            new DropIndexOperation { Table = "users", Name = "ix_users_email" },
            new DropForeignKeyOperation { Table = "agents", Name = "fk_agents_users_user_id" },
            new RenameColumnOperation { Table = "users", Name = "theme", NewName = "appearance" },
            new InsertDataOperation { Table = "roles", Columns = ["code"], Values = new object[,] { { "Strategist" } } },

            // A column that becomes required holds what it held.
            new AlterColumnOperation
            {
                Table = "agents", Name = "mode", ClrType = typeof(string), ColumnType = "text", IsNullable = false,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), ColumnType = "text", IsNullable = true },
            },
            new SqlOperation { Sql = "INSERT INTO agent_state_changes (id, agent_id) SELECT gen_random_uuid(), id FROM agents" },
            new SqlOperation { Sql = "ALTER TABLE users DROP CONSTRAINT ck_users_email" },

            // What names the words, and does none of it itself: a trigger, and the function that it calls.
            new SqlOperation { Sql = "CREATE TRIGGER prompt_templates_keep BEFORE UPDATE OR DELETE ON prompt_templates FOR EACH ROW EXECUTE FUNCTION keep()" },
            new SqlOperation { Sql = "CREATE FUNCTION keep() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'no UPDATE and no DELETE'; END $$" },

            // A column whose name begins as a word of the rule does.
            new SqlOperation { Sql = "WITH latest AS (SELECT max(updated_at) AS at FROM agents) INSERT INTO marks (at) SELECT at FROM latest" },
        ];

        Assert.Empty(DestructiveRules.FindIn(operations));
    }

    /// <summary>The migrations of the application, in order: each with whether it is marked, and with what the rule finds in it.</summary>
    private static List<(string Id, bool Marked, IReadOnlyList<string> Loses)> Migrations()
    {
        using var context = ModelOnly.Context();

        var assembly = context.GetService<IMigrationsAssembly>();
        var provider = context.Database.ProviderName!;

        var migrations = assembly.Migrations
            .Select(migration => (
                Id: migration.Key,
                Marked: migration.Value.IsDefined(typeof(DestructiveMigrationAttribute), inherit: false),
                Loses: DestructiveRules.FindIn(assembly.CreateMigration(migration.Value, provider).UpOperations)))
            .ToList();

        Assert.NotEmpty(migrations);

        return migrations;
    }
}
