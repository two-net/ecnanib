using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is set by hand. The generated one was the empty text, which is no
            // theme. An account that there is when this is applied has chosen nothing, and looks
            // as the device of its user is set, as a new account does (FR-THM-1). See
            // docs/themes.md.
            migrationBuilder.AddColumn<string>(
                name: "theme",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "System");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "theme",
                table: "users");
        }
    }
}
