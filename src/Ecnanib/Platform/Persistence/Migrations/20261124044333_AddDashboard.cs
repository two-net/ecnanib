using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "notifications",
                type: "text",
                nullable: true);

            // Changed by hand: the two statements below. A notification that is stored when
            // this is applied said its mode in its text alone. From here on it is stored with
            // the mode that whoever raises it says, and keeps it when its agent is switched or
            // deleted (FR-BIN-11). See docs/notifications.md.
            //
            // One that is about an agent is of the mode that the agent is in now: an agent is
            // switched while it is stopped and holds nothing, so what was said of it before is
            // of the mode that it was in then only where it was switched since. The agent is
            // found by its owner as well, as every row of a user is. One about an agent that is
            // deleted since stays without a mode.
            migrationBuilder.Sql(
                """
                UPDATE notifications AS notification
                SET mode = agent.mode
                FROM agents AS agent
                WHERE agent.id = notification.agent_id
                  AND agent.user_id = notification.user_id;
                """);

            // One about a Binance key names no agent, and says the environment of the key in
            // words that are always the same. A key of an AI provider is of no mode.
            migrationBuilder.Sql(
                """
                UPDATE notifications
                SET mode = 'Demo'
                WHERE agent_id IS NULL AND type = 'KeyInvalid' AND content LIKE 'Your Binance key for Demo %';

                UPDATE notifications
                SET mode = 'Live'
                WHERE agent_id IS NULL AND type = 'KeyInvalid' AND content LIKE 'Your Binance key for Live %';
                """);

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_user_id_mode_closed_at",
                table: "decision_cycles",
                columns: new[] { "user_id", "mode", "closed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_decision_cycles_user_id_mode_closed_at",
                table: "decision_cycles");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "notifications");
        }
    }
}
