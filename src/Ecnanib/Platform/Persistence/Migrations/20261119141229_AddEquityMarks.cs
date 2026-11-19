using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEquityMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equity_marks",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset = table.Column<string>(type: "text", nullable: false),
                    capital = table.Column<decimal>(type: "numeric", nullable: false),
                    day_began_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_at_day_start = table.Column<decimal>(type: "numeric", nullable: false),
                    peak_result = table.Column<decimal>(type: "numeric", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_equity_marks", x => new { x.agent_id, x.mode });
                    table.ForeignKey(
                        name: "fk_equity_marks_agents_agent_id_user_id",
                        columns: x => new { x.agent_id, x.user_id },
                        principalTable: "agents",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_equity_marks_agent_id_user_id",
                table: "equity_marks",
                columns: new[] { "agent_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equity_marks");
        }
    }
}
