using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddPositionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "made_at_exchange",
                table: "positions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quote_asset",
                table: "positions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "timeframe",
                table: "positions",
                type: "text",
                nullable: false,
                defaultValue: "");

            // A position that is stored says neither the asset that its symbol is priced in nor
            // the timeframe that its agent worked on. Both are what its agent says now, which is
            // what the agent had when the position was opened, unless its user gave it another
            // since. A position in a symbol that is not priced in the asset of its agent as it is
            // now says no asset: it is in no result of the agent, as it was in none before. See
            // docs/orders.md.
            migrationBuilder.Sql(
                """
                UPDATE positions
                SET timeframe = agents.timeframe,
                    quote_asset = CASE
                        WHEN right(positions.symbol, length(agents.quote_asset)) = agents.quote_asset THEN agents.quote_asset
                        ELSE ''
                    END
                FROM agents
                WHERE agents.id = positions.agent_id;
                """);

            migrationBuilder.AddColumn<decimal>(
                name: "fee_value",
                table: "fills",
                type: "numeric",
                nullable: true);

            // The fee of an execution that is stored is given the value that the equity of its
            // agent counted it by until now: what it is in the asset that the symbol is priced
            // in, and what its execution paid for it in the asset that is traded (FR-BIN-10). A
            // fee in any other asset was in no figure, and has no value: it is not valued any
            // more.
            migrationBuilder.Sql(
                """
                UPDATE fills
                SET fee_value = CASE
                    WHEN fills.fee = 0 THEN 0
                    WHEN fills.fee_asset = agents.quote_asset THEN round(fills.fee, 12)
                    WHEN orders.symbol = fills.fee_asset || agents.quote_asset THEN round(fills.fee * fills.price, 12)
                END
                FROM orders
                JOIN agents ON agents.id = orders.agent_id
                WHERE orders.id = fills.order_id
                    AND right(orders.symbol, length(agents.quote_asset)) = agents.quote_asset;
                """);

            migrationBuilder.CreateTable(
                name: "equity_snapshots",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset = table.Column<string>(type: "text", nullable: false),
                    capital = table.Column<decimal>(type: "numeric", nullable: false),
                    equity = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_equity_snapshots", x => new { x.agent_id, x.mode, x.at });
                    table.ForeignKey(
                        name: "fk_equity_snapshots_agents_agent_id_user_id",
                        columns: x => new { x.agent_id, x.user_id },
                        principalTable: "agents",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "position_results",
                columns: table => new
                {
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset = table.Column<string>(type: "text", nullable: false),
                    gross_result = table.Column<decimal>(type: "numeric", nullable: false),
                    fees = table.Column<decimal>(type: "numeric", nullable: false),
                    funding = table.Column<decimal>(type: "numeric", nullable: false),
                    net_result = table.Column<decimal>(type: "numeric", nullable: false),
                    risk = table.Column<decimal>(type: "numeric", nullable: true),
                    result_in_r = table.Column<decimal>(type: "numeric", nullable: true),
                    holding_seconds = table.Column<long>(type: "bigint", nullable: false),
                    complete = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_results", x => x.position_id);
                    table.ForeignKey(
                        name: "fk_position_results_positions_position_id_user_id",
                        columns: x => new { x.position_id, x.user_id },
                        principalTable: "positions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "position_roles",
                columns: table => new
                {
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_roles", x => new { x.position_id, x.role });
                    table.ForeignKey(
                        name: "fk_position_roles_ai_models_model_id",
                        column: x => x.model_id,
                        principalTable: "ai_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_position_roles_positions_position_id_user_id",
                        columns: x => new { x.position_id, x.user_id },
                        principalTable: "positions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_position_roles_roles_role",
                        column: x => x.role,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            // A position that is stored names the models that hold the roles of its agent now:
            // nothing else says which held them when it was opened (FR-EXP-1).
            migrationBuilder.Sql(
                """
                INSERT INTO position_roles (position_id, role, user_id, model_id)
                SELECT positions.id, agent_roles.role, positions.user_id, agent_roles.model_id
                FROM positions
                JOIN agent_roles ON agent_roles.agent_id = positions.agent_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_fills_order_id",
                table: "fills",
                column: "order_id",
                filter: "fee_value IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_equity_snapshots_agent_id_user_id",
                table: "equity_snapshots",
                columns: new[] { "agent_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_position_results_position_id_user_id",
                table: "position_results",
                columns: new[] { "position_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_position_roles_model_id",
                table: "position_roles",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_position_roles_position_id_user_id",
                table: "position_roles",
                columns: new[] { "position_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_position_roles_role",
                table: "position_roles",
                column: "role");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equity_snapshots");

            migrationBuilder.DropTable(
                name: "position_results");

            migrationBuilder.DropTable(
                name: "position_roles");

            migrationBuilder.DropIndex(
                name: "ix_fills_order_id",
                table: "fills");

            migrationBuilder.DropColumn(
                name: "made_at_exchange",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "quote_asset",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "timeframe",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "fee_value",
                table: "fills");
        }
    }
}
