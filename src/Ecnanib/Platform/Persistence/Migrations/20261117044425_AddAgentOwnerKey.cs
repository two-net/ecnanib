using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentOwnerKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_agents_id_user_id",
                table: "agents",
                columns: new[] { "id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "ak_agents_id_user_id",
                table: "agents");
        }
    }
}
