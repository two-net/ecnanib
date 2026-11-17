using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleOutputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decision_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_cycles", x => x.id);
                    table.UniqueConstraint("ak_decision_cycles_id_user_id", x => new { x.id, x.user_id });
                    table.ForeignKey(
                        name: "fk_decision_cycles_agents_agent_id_user_id",
                        columns: x => new { x.agent_id, x.user_id },
                        principalTable: "agents",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_outputs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_template_id = table.Column<int>(type: "integer", nullable: false),
                    market_template_id = table.Column<int>(type: "integer", nullable: false),
                    timeframe_template_id = table.Column<int>(type: "integer", nullable: false),
                    profile_template_id = table.Column<int>(type: "integer", nullable: false),
                    called_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    live_searches = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    validation = table.Column<string>(type: "text", nullable: false),
                    rejection = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_outputs", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_outputs_ai_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_outputs_decision_cycles_cycle_id_user_id",
                        columns: x => new { x.cycle_id, x.user_id },
                        principalTable: "decision_cycles",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_outputs_prompt_templates_market_template_id",
                        column: x => x.market_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_outputs_prompt_templates_profile_template_id",
                        column: x => x.profile_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_outputs_prompt_templates_role_template_id",
                        column: x => x.role_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_outputs_prompt_templates_timeframe_template_id",
                        column: x => x.timeframe_template_id,
                        principalTable: "prompt_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_outputs_roles_role",
                        column: x => x.role,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_agent_id_user_id_closed_at",
                table: "decision_cycles",
                columns: new[] { "agent_id", "user_id", "closed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_cycle_id_user_id_role_attempt",
                table: "role_outputs",
                columns: new[] { "cycle_id", "user_id", "role", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_market_template_id",
                table: "role_outputs",
                column: "market_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_model_id",
                table: "role_outputs",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_profile_template_id",
                table: "role_outputs",
                column: "profile_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_role",
                table: "role_outputs",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_role_template_id",
                table: "role_outputs",
                column: "role_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_timeframe_template_id",
                table: "role_outputs",
                column: "timeframe_template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_outputs");

            migrationBuilder.DropTable(
                name: "decision_cycles");
        }
    }
}
