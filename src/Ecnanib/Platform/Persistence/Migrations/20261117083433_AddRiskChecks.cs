using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "risk_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verdict = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_checks", x => x.id);
                    table.UniqueConstraint("ak_risk_checks_id_user_id", x => new { x.id, x.user_id });
                    table.ForeignKey(
                        name: "fk_risk_checks_decision_cycles_cycle_id_user_id",
                        columns: x => new { x.cycle_id, x.user_id },
                        principalTable: "decision_cycles",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "risk_check_rules",
                columns: table => new
                {
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    measured = table.Column<decimal>(type: "numeric", nullable: true),
                    threshold = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_check_rules", x => new { x.check_id, x.user_id, x.rule });
                    table.ForeignKey(
                        name: "fk_risk_check_rules_risk_checks_check_id_user_id",
                        columns: x => new { x.check_id, x.user_id },
                        principalTable: "risk_checks",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_risk_checks_cycle_id_user_id_sequence",
                table: "risk_checks",
                columns: new[] { "cycle_id", "user_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "risk_check_rules");

            migrationBuilder.DropTable(
                name: "risk_checks");
        }
    }
}
