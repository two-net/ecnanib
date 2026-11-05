using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false, computedColumnSql: "lower(name)", stored: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quote_asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exchange_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    auto_execute_risk_reducing = table.Column<bool>(type: "boolean", nullable: false),
                    capital_allocation = table.Column<decimal>(type: "numeric", nullable: true),
                    risk_max_risk_per_trade_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_max_position_notional = table.Column<decimal>(type: "numeric", nullable: true),
                    risk_max_leverage = table.Column<int>(type: "integer", nullable: false),
                    risk_daily_loss_limit_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_weekly_loss_limit_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_consecutive_losses_before_pause = table.Column<int>(type: "integer", nullable: false),
                    risk_min_reward_to_risk = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_max_new_entries_per_day = table.Column<int>(type: "integer", nullable: false),
                    risk_min_stop_distance_atr = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_max_stop_distance_atr = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_liquidation_buffer_multiple = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_funding_filter_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_cooldown_cycles_after_loss = table.Column<int>(type: "integer", nullable: false),
                    budget_daily_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    budget_monthly_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    budget_daily_tokens = table.Column<long>(type: "bigint", nullable: true),
                    budget_monthly_tokens = table.Column<long>(type: "bigint", nullable: true),
                    experience_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reflections_in_prompt = table.Column<int>(type: "integer", nullable: false),
                    custom_instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_role_assignments",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fallback_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fallback_model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_role_assignments", x => new { x.agent_id, x.role });
                    table.ForeignKey(
                        name: "fk_agent_role_assignments_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agents_exchange_credential_id",
                table: "agents",
                column: "exchange_credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_trading_slot",
                table: "agents",
                columns: new[] { "exchange_credential_id", "market", "symbol" },
                unique: true,
                filter: "status IN ('Active', 'Paused', 'Error')");

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id",
                table: "agents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id_name_key",
                table: "agents",
                columns: new[] { "user_id", "name_key" },
                unique: true,
                filter: "status <> 'Archived'");
        }
    }
}
