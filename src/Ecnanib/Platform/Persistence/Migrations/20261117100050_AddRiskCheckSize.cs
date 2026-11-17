using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskCheckSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "leverage",
                table: "risk_checks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "quantity",
                table: "risk_checks",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "leverage",
                table: "risk_checks");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "risk_checks");
        }
    }
}
