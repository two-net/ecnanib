using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddDecisionTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decision_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    candle_close = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    catch_up = table.Column<bool>(type: "boolean", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    input_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    loss_limits = table.Column<string>(type: "jsonb", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost = table.Column<decimal>(type: "numeric", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_cycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fallback = table.Column<bool>(type: "boolean", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_message = table.Column<string>(type: "text", nullable: true),
                    instructions = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    answer = table.Column<string>(type: "text", nullable: true),
                    output = table.Column<string>(type: "jsonb", nullable: true),
                    violations = table.Column<string>(type: "jsonb", nullable: true),
                    templates = table.Column<string>(type: "jsonb", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost = table.Column<decimal>(type: "numeric", nullable: true),
                    latency = table.Column<TimeSpan>(type: "interval", nullable: false),
                    provider_attempts = table.Column<int>(type: "integer", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_calls", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_calls_decision_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "decision_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cycle_roles",
                columns: table => new
                {
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    used_fallback = table.Column<bool>(type: "boolean", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cycle_roles", x => new { x.cycle_id, x.role });
                    table.ForeignKey(
                        name: "fk_cycle_roles_decision_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "decision_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "risk_evaluations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    intent = table.Column<string>(type: "jsonb", nullable: false),
                    caps = table.Column<string>(type: "jsonb", nullable: false),
                    effective = table.Column<string>(type: "jsonb", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    sized_order = table.Column<string>(type: "jsonb", nullable: true),
                    breach = table.Column<string>(type: "jsonb", nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "fk_risk_evaluations_decision_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "decision_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_cycle_id",
                table: "ai_calls",
                column: "cycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_agent_id_candle_close",
                table: "decision_cycles",
                columns: new[] { "agent_id", "candle_close" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_started_at",
                table: "decision_cycles",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_user_id_agent_id_started_at",
                table: "decision_cycles",
                columns: new[] { "user_id", "agent_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_risk_evaluations_cycle_id",
                table: "risk_evaluations",
                column: "cycle_id",
                unique: true);
        }
    }
}
