using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_live",
                table: "orders");

            migrationBuilder.AddColumn<string>(
                name: "closing_for",
                table: "protections",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cycle_id",
                table: "protections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "protections",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ended_at",
                table: "protections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "entry_price",
                table: "protections",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "id_candle_close",
                table: "protections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "id_index",
                table: "protections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "opened_at",
                table: "protections",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "rested",
                table: "protections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stop_lost_at",
                table: "protections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "stop_price",
                table: "protections",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "target_price",
                table: "protections",
                type: "numeric",
                nullable: true);

            // Each protection so far took its prices, cost, cycle and client order ID base from its entry.
            migrationBuilder.Sql(
                """
                UPDATE protections p SET
                    stop_price = e.protective_stop,
                    target_price = e.target,
                    entry_price = COALESCE(e.average_price, e.price, e.protective_stop),
                    opened_at = COALESCE(e.filled_at, p.created_at),
                    cycle_id = e.cycle_id,
                    id_candle_close = e.candle_close,
                    id_index = p.sequence,
                    rested = p.status IN ('Resting', 'Ended'),
                    closing_for = CASE WHEN p.status = 'Closing' THEN 'Unprotected' END,
                    end_reason = CASE WHEN p.status = 'Closed' THEN 'Unprotected' END,
                    ended_at = CASE WHEN p.status IN ('Closed', 'Ended') THEN p.updated_at END
                FROM orders e
                WHERE e.id = p.entry_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_protections_held",
                table: "protections",
                column: "agent_id",
                filter: "status IN ('Pending', 'Resting', 'Closing')");

            migrationBuilder.CreateIndex(
                name: "ix_orders_live",
                table: "orders",
                column: "status",
                filter: "status IN ('Sending', 'Unknown', 'Open', 'PartiallyFilled', 'Triggered')");
        }
    }
}
