using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is set by hand, as the one of the theme was. The generated one was the
            // empty text, which is no choice. An account that there is when this is applied has
            // chosen nothing, and its pages move as the device of its user asks, as those of a
            // new account do (FR-ANI-4). See docs/motion.md.
            migrationBuilder.AddColumn<string>(
                name: "motion",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "System");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "motion",
                table: "users");
        }
    }
}
