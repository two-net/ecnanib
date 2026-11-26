using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeDisplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The default is set by hand, as the ones of the theme and of the motion were. The
            // generated one was the empty text, which is no choice. An account that there is
            // when this is applied has chosen nothing, and reads a time in the time zone of the
            // device that its user signs in on, as a new account does (NFR-USA-3). See
            // docs/times.md.
            migrationBuilder.AddColumn<string>(
                name: "time_display",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "Device");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "time_display",
                table: "users");
        }
    }
}
