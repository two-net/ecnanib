using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddTradeJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "triggered_order_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_asset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quote_asset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    side = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric", nullable: false),
                    initial_stop = table.Column<decimal>(type: "numeric", nullable: false),
                    exit_quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    exit_price = table.Column<decimal>(type: "numeric", nullable: true),
                    unpriced_quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    fees = table.Column<decimal>(type: "numeric", nullable: true),
                    funding = table.Column<decimal>(type: "numeric", nullable: true),
                    realized_pnl = table.Column<decimal>(type: "numeric", nullable: true),
                    r_multiple = table.Column<decimal>(type: "numeric", nullable: true),
                    adverse_excursion = table.Column<decimal>(type: "numeric", nullable: true),
                    favorable_excursion = table.Column<decimal>(type: "numeric", nullable: true),
                    exit_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settle_attempts = table.Column<int>(type: "integer", nullable: false),
                    settle_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trades", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trade_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exchange_trade_id = table.Column<long>(type: "bigint", nullable: false),
                    exchange_order_id = table.Column<long>(type: "bigint", nullable: false),
                    side = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    fee = table.Column<decimal>(type: "numeric", nullable: false),
                    fee_asset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fee_quote = table.Column<decimal>(type: "numeric", nullable: true),
                    maker = table.Column<bool>(type: "boolean", nullable: false),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fills", x => x.id);
                    table.ForeignKey(
                        name: "fk_fills_trades_trade_id",
                        column: x => x.trade_id,
                        principalTable: "trades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "funding_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trade_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exchange_transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rate = table.Column<decimal>(type: "numeric", nullable: true),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    payment_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    asset = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_funding_payments_trades_trade_id",
                        column: x => x.trade_id,
                        principalTable: "trades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trade_cycles",
                columns: table => new
                {
                    trade_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trade_cycles", x => new { x.trade_id, x.cycle_id });
                    table.ForeignKey(
                        name: "fk_trade_cycles_trades_trade_id",
                        column: x => x.trade_id,
                        principalTable: "trades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fills_order_id",
                table: "fills",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_fills_trade_id_source_exchange_trade_id",
                table: "fills",
                columns: new[] { "trade_id", "source", "exchange_trade_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_funding_payments_trade_id_exchange_transaction_id",
                table: "funding_payments",
                columns: new[] { "trade_id", "exchange_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trade_cycles_cycle_id",
                table: "trade_cycles",
                column: "cycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_trades_entry_id",
                table: "trades",
                column: "entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trades_open",
                table: "trades",
                column: "agent_id",
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_trades_unsettled",
                table: "trades",
                column: "settle_after",
                filter: "status = 'Closed' AND settled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trades_user_id_agent_id_closed_at",
                table: "trades",
                columns: new[] { "user_id", "agent_id", "closed_at" });
        }
    }
}
