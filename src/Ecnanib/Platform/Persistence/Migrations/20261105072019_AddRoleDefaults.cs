using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddRoleDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_defaults",
                columns: table => new
                {
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_defaults", x => x.role);
                    table.ForeignKey(
                        name: "fk_role_defaults_providers_provider",
                        column: x => x.provider,
                        principalTable: "providers",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_defaults_provider",
                table: "role_defaults",
                column: "provider");
        }
    }
}
