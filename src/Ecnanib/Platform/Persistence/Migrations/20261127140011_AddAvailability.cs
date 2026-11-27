using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    announced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    in_advance = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "up_spans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    began_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    beat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_up_spans", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_ends_at",
                table: "maintenance_windows",
                column: "ends_at");

            migrationBuilder.CreateIndex(
                name: "ix_up_spans_began_at",
                table: "up_spans",
                column: "began_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_windows");

            migrationBuilder.DropTable(
                name: "up_spans");
        }
    }
}
