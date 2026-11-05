using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candle_cache",
                columns: table => new
                {
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    open_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<decimal>(type: "numeric", nullable: false),
                    high = table.Column<decimal>(type: "numeric", nullable: false),
                    low = table.Column<decimal>(type: "numeric", nullable: false),
                    close = table.Column<decimal>(type: "numeric", nullable: false),
                    volume = table.Column<decimal>(type: "numeric", nullable: false),
                    quote_volume = table.Column<decimal>(type: "numeric", nullable: false),
                    trades = table.Column<long>(type: "bigint", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candle_cache", x => new { x.market, x.environment, x.symbol, x.interval, x.open_time });
                });

            migrationBuilder.CreateTable(
                name: "exchange_symbols",
                columns: table => new
                {
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    base_asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quote_asset = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    contract_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    order_types = table.Column<string[]>(type: "text[]", nullable: false),
                    min_price = table.Column<decimal>(type: "numeric", nullable: true),
                    max_price = table.Column<decimal>(type: "numeric", nullable: true),
                    tick_size = table.Column<decimal>(type: "numeric", nullable: true),
                    min_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    max_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    step_size = table.Column<decimal>(type: "numeric", nullable: true),
                    market_min_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    market_max_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    market_step_size = table.Column<decimal>(type: "numeric", nullable: true),
                    min_notional = table.Column<decimal>(type: "numeric", nullable: true),
                    max_notional = table.Column<decimal>(type: "numeric", nullable: true),
                    refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_symbols", x => new { x.market, x.environment, x.symbol });
                });

            migrationBuilder.CreateIndex(
                name: "ix_candle_cache_fetched_at",
                table: "candle_cache",
                column: "fetched_at");
        }
    }
}
