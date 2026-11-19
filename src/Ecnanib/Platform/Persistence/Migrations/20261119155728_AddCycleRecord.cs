using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCycleRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "data_package",
                table: "decision_cycles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "no_entry",
                table: "decision_cycles",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_cycle_id",
                table: "orders",
                column: "cycle_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_cycle_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "data_package",
                table: "decision_cycles");

            migrationBuilder.DropColumn(
                name: "no_entry",
                table: "decision_cycles");
        }
    }
}
