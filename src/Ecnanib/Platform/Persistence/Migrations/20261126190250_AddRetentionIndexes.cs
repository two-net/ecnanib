using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_called_at",
                table: "role_outputs",
                column: "called_at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at_all",
                table: "notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_equity_snapshots_at",
                table: "equity_snapshots",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_decision_cycles_closed_at",
                table: "decision_cycles",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_role_outputs_called_at",
                table: "role_outputs");

            migrationBuilder.DropIndex(
                name: "ix_notifications_created_at_all",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_equity_snapshots_at",
                table: "equity_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_decision_cycles_closed_at",
                table: "decision_cycles");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events");
        }
    }
}
