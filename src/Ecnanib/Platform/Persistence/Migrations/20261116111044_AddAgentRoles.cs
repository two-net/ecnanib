using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_roles",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_roles", x => new { x.agent_id, x.role });
                    table.ForeignKey(
                        name: "fk_agent_roles_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_roles_ai_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_roles_roles_role",
                        column: x => x.role,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_roles_model_id",
                table: "agent_roles",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_roles_role",
                table: "agent_roles",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "ix_agent_roles_user_id",
                table: "agent_roles",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_roles");
        }
    }
}
