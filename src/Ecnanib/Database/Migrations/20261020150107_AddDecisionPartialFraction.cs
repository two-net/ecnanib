using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionPartialFraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "partial_fraction",
                table: "decisions",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
