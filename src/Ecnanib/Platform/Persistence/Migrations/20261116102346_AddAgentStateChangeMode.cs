using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentStateChangeMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Changed by hand. As it was generated, the column had an empty text as its default,
            // which is the name of no mode. It is added without a value instead, filled, and
            // only then required.
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "agent_state_changes",
                type: "text",
                nullable: true);

            // A change that is stored when this is applied happened in the mode that its agent
            // is in: until now nothing switched the mode of an agent, so every agent is in the
            // mode it was created in. From here on a change is stored with the mode the agent is
            // in then, and keeps it when the agent is switched (FR-BIN-11). See docs/agents.md.
            migrationBuilder.Sql(
                """
                UPDATE agent_state_changes AS change
                SET mode = agent.mode
                FROM agents AS agent
                WHERE agent.id = change.agent_id;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "mode",
                table: "agent_state_changes",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mode",
                table: "agent_state_changes");
        }
    }
}
