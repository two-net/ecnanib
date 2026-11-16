using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentStateChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_state_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    cause = table.Column<string>(type: "text", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_state_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_state_changes_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_state_changes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_state_changes_agent_id_changed_at",
                table: "agent_state_changes",
                columns: new[] { "agent_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_state_changes_user_id",
                table: "agent_state_changes",
                column: "user_id");

            // Added by hand. An agent that is stored when this is applied has no change yet:
            // until now only its row said its state, why, and since when. Each gets that as its
            // first change, so that the row of an agent and the latest of its changes say the
            // same for every agent. The id is the database's here, and not one of the
            // application: an agent has one such row, so their order does not matter. See
            // docs/agents.md.
            migrationBuilder.Sql(
                """
                INSERT INTO agent_state_changes (id, agent_id, user_id, state, cause, changed_at)
                SELECT gen_random_uuid(), id, user_id, state, state_cause, state_changed_at FROM agents;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_state_changes");
        }
    }
}
