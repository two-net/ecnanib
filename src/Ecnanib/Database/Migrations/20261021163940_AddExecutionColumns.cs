using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "remaining_size",
                table: "trades",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "exchange_algo_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "fee",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "realized_pnl",
                table: "orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<JsonElement>(
                name: "execution_check",
                table: "decisions",
                type: "jsonb",
                nullable: true);

            // Positions opened before this column: the whole size is still open.
            migrationBuilder.Sql("UPDATE trades SET remaining_size = size WHERE closed_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_trades_remaining_size",
                table: "trades",
                sql: "remaining_size >= 0 AND remaining_size <= size");

            migrationBuilder.CreateIndex(
                name: "ix_orders_exchange_algo_id",
                table: "orders",
                column: "exchange_algo_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
