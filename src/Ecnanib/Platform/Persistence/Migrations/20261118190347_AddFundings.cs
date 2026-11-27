using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddFundings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "funding_due_at",
                table: "positions",
                type: "timestamp with time zone",
                nullable: true);

            // A position of the Futures market that is open now has received and paid funding
            // since it was opened, and none of it is stored: its funding is read from then on,
            // as that of a position that is opened from here on is (FR-BIN-F4). A position that
            // is closed is left as it is: the exchange is not asked about it any more. See
            // docs/orders.md.
            migrationBuilder.Sql(
                """
                UPDATE positions
                SET funding_due_at = opened_at
                WHERE market = 'Futures' AND closed_at IS NULL;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_positions_id_user_id",
                table: "positions",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateTable(
                name: "fundings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    asset = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fundings", x => x.id);
                    table.ForeignKey(
                        name: "fk_fundings_positions_position_id_user_id",
                        columns: x => new { x.position_id, x.user_id },
                        principalTable: "positions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_positions_funding_due_at",
                table: "positions",
                column: "funding_due_at",
                filter: "funding_due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_fundings_position_id_transaction_id",
                table: "fundings",
                columns: new[] { "position_id", "transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fundings_position_id_user_id",
                table: "fundings",
                columns: new[] { "position_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fundings");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_positions_id_user_id",
                table: "positions");

            migrationBuilder.DropIndex(
                name: "ix_positions_funding_due_at",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "funding_due_at",
                table: "positions");
        }
    }
}
