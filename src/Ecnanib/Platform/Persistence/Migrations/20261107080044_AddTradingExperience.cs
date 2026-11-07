using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddTradingExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cycle_attributions",
                columns: table => new
                {
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cycle_attributions", x => new { x.cycle_id, x.role });
                });

            migrationBuilder.CreateTable(
                name: "experience_progress",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    built_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experience_progress", x => new { x.user_id, x.timeframe });
                });

            migrationBuilder.CreateTable(
                name: "performance_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cycles = table.Column<long>(type: "bigint", nullable: false),
                    answered_cycles = table.Column<long>(type: "bigint", nullable: false),
                    fallback_cycles = table.Column<long>(type: "bigint", nullable: false),
                    sent = table.Column<long>(type: "bigint", nullable: false),
                    not_sent = table.Column<long>(type: "bigint", nullable: false),
                    answered = table.Column<long>(type: "bigint", nullable: false),
                    contract_violations = table.Column<long>(type: "bigint", nullable: false),
                    timeouts = table.Column<long>(type: "bigint", nullable: false),
                    refusals = table.Column<long>(type: "bigint", nullable: false),
                    errors = table.Column<long>(type: "bigint", nullable: false),
                    truncated = table.Column<long>(type: "bigint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost = table.Column<decimal>(type: "numeric", nullable: false),
                    unpriced_tokens = table.Column<long>(type: "bigint", nullable: false),
                    latency_buckets = table.Column<long[]>(type: "bigint[]", nullable: false),
                    holds = table.Column<long>(type: "bigint", nullable: false),
                    entries = table.Column<long>(type: "bigint", nullable: false),
                    exits = table.Column<long>(type: "bigint", nullable: false),
                    adjustments = table.Column<long>(type: "bigint", nullable: false),
                    approvals = table.Column<long>(type: "bigint", nullable: false),
                    modifications = table.Column<long>(type: "bigint", nullable: false),
                    rejections = table.Column<long>(type: "bigint", nullable: false),
                    bullish_calls = table.Column<long>(type: "bigint", nullable: false),
                    bearish_calls = table.Column<long>(type: "bigint", nullable: false),
                    hits = table.Column<long>(type: "bigint", nullable: false),
                    misses = table.Column<long>(type: "bigint", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_performance_snapshots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cycle_attributions_user_id_agent_id",
                table: "cycle_attributions",
                columns: new[] { "user_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_performance_snapshots_key",
                table: "performance_snapshots",
                columns: new[] { "user_id", "agent_id", "environment", "market", "symbol", "timeframe", "role", "provider", "model_id", "period_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_performance_snapshots_user_id_environment_period_start",
                table: "performance_snapshots",
                columns: new[] { "user_id", "environment", "period_start" });
        }
    }
}
