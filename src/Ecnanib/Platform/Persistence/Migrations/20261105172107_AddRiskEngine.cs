using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddRiskEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "percent_average_price_minutes",
                table: "exchange_symbols",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "percent_buy_down",
                table: "exchange_symbols",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "percent_buy_up",
                table: "exchange_symbols",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "percent_sell_down",
                table: "exchange_symbols",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "percent_sell_up",
                table: "exchange_symbols",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "loss_limit_until",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
