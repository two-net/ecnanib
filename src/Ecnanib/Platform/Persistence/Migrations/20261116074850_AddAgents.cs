using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgents : Migration
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
                    name = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    market = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "text", nullable: false),
                    quote_asset = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    allocated_capital = table.Column<decimal>(type: "numeric", nullable: false),
                    risk_per_trade_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    leverage_cap = table.Column<int>(type: "integer", nullable: false),
                    daily_loss_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    drawdown_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    max_entry_distance_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    state_cause = table.Column<string>(type: "text", nullable: false),
                    state_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id_mode_market_symbol",
                table: "agents",
                columns: new[] { "user_id", "mode", "market", "symbol" },
                unique: true,
                filter: "state NOT IN ('Draft', 'Stopped')");

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id_name",
                table: "agents",
                columns: new[] { "user_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
