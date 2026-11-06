using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddOrderExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "execution",
                table: "decision_cycles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    candle_close = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    system_evaluation = table.Column<string>(type: "jsonb", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    leg = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    protection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    list_client_order_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    client_order_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    exchange_order_id = table.Column<long>(type: "bigint", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    side = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: true),
                    stop_price = table.Column<decimal>(type: "numeric", nullable: true),
                    reduce_only = table.Column<bool>(type: "boolean", nullable: false),
                    protective_stop = table.Column<decimal>(type: "numeric", nullable: true),
                    target = table.Column<decimal>(type: "numeric", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    executed_quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    average_price = table.Column<decimal>(type: "numeric", nullable: true),
                    net_quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    net_basis = table.Column<decimal>(type: "numeric", nullable: true),
                    resolved = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<int>(type: "integer", nullable: true),
                    settles_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "protections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    protect_by = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    target_dropped = table.Column<bool>(type: "boolean", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_protections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_client_order_id",
                table: "orders",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_entry_id",
                table: "orders",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_live",
                table: "orders",
                column: "status",
                filter: "status IN ('Sending', 'Unknown', 'Open', 'PartiallyFilled')");

            migrationBuilder.CreateIndex(
                name: "ix_orders_unresolved_entries",
                table: "orders",
                column: "agent_id",
                filter: "leg = 'Entry' AND NOT resolved");

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id_agent_id_status",
                table: "orders",
                columns: new[] { "user_id", "agent_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_protections_entry_id_sequence",
                table: "protections",
                columns: new[] { "entry_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_protections_unsettled",
                table: "protections",
                column: "status",
                filter: "status IN ('Pending', 'Closing')");

            migrationBuilder.CreateIndex(
                name: "ix_protections_user_id_agent_id_status",
                table: "protections",
                columns: new[] { "user_id", "agent_id", "status" });
        }
    }
}
