using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddCycleKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Changed by hand. As it was generated, the column had an empty text as its default,
            // which is the name of no kind. It is added without a value instead, filled, and
            // only then required.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "decision_cycles",
                type: "text",
                nullable: true);

            // A cycle that is stored when this is applied was started by the close of a candle:
            // until now nothing else started one. From here on a cycle is stored with what
            // started it, so that a test cycle, which its user ran and which placed no orders,
            // is told apart (FR-CYC-12). See "A test cycle" in docs/cycles.md.
            migrationBuilder.Sql(
                """
                UPDATE decision_cycles
                SET kind = 'Scheduled';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                table: "decision_cycles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                table: "decision_cycles");
        }
    }
}
