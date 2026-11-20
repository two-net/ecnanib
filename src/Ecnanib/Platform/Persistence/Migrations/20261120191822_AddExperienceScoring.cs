using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExperienceScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analyst_calls",
                columns: table => new
                {
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    market = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    later_price = table.Column<decimal>(type: "numeric", nullable: true),
                    scored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analyst_calls", x => new { x.cycle_id, x.role });
                    table.ForeignKey(
                        name: "fk_analyst_calls_ai_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_analyst_calls_decision_cycles_cycle_id_user_id",
                        columns: x => new { x.cycle_id, x.user_id },
                        principalTable: "decision_cycles",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_analyst_calls_roles_role",
                        column: x => x.role,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shadow_trades",
                columns: table => new
                {
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    market = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric", nullable: false),
                    resting = table.Column<bool>(type: "boolean", nullable: false),
                    stop_loss = table.Column<decimal>(type: "numeric", nullable: false),
                    take_profit = table.Column<decimal>(type: "numeric", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    result_in_r = table.Column<decimal>(type: "numeric", nullable: true),
                    exited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shadow_trades", x => x.cycle_id);
                    table.ForeignKey(
                        name: "fk_shadow_trades_ai_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shadow_trades_decision_cycles_cycle_id_user_id",
                        columns: x => new { x.cycle_id, x.user_id },
                        principalTable: "decision_cycles",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analyst_calls_cycle_id_user_id",
                table: "analyst_calls",
                columns: new[] { "cycle_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_analyst_calls_due_at",
                table: "analyst_calls",
                column: "due_at",
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_analyst_calls_model_id",
                table: "analyst_calls",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_analyst_calls_role",
                table: "analyst_calls",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "ix_analyst_calls_user_id_closed_at",
                table: "analyst_calls",
                columns: new[] { "user_id", "closed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_shadow_trades_cycle_id_user_id",
                table: "shadow_trades",
                columns: new[] { "cycle_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shadow_trades_due_at",
                table: "shadow_trades",
                column: "due_at",
                filter: "outcome IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shadow_trades_model_id",
                table: "shadow_trades",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_shadow_trades_user_id_closed_at",
                table: "shadow_trades",
                columns: new[] { "user_id", "closed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analyst_calls");

            migrationBuilder.DropTable(
                name: "shadow_trades");
        }
    }
}
