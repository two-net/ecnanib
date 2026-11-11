using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    last_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs");
        }
    }
}
