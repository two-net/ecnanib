using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    end = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    intent = table.Column<string>(type: "jsonb", nullable: false),
                    limit_ttl_candles = table.Column<int>(type: "integer", nullable: true),
                    proposal = table.Column<string>(type: "jsonb", nullable: false),
                    revalidation = table.Column<string>(type: "jsonb", nullable: true),
                    revalidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    execution = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_approvals_decision_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "decision_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_agent_id_approving",
                table: "approvals",
                column: "agent_id",
                unique: true,
                filter: "status = 'Approving'");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_agent_id_pending",
                table: "approvals",
                column: "agent_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_cycle_id",
                table: "approvals",
                column: "cycle_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_approvals_pending_expires_at",
                table: "approvals",
                column: "expires_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_user_id_created_at",
                table: "approvals",
                columns: new[] { "user_id", "created_at" });
        }
    }
}
