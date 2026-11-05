using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddCatalogAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "provider",
                table: "role_defaults",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "model_overrides",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    image_input = table.Column<bool>(type: "boolean", nullable: true),
                    structured_output = table.Column<bool>(type: "boolean", nullable: true),
                    tool_use = table.Column<bool>(type: "boolean", nullable: true),
                    live_search = table.Column<bool>(type: "boolean", nullable: true),
                    reasoning_effort = table.Column<bool>(type: "boolean", nullable: true),
                    input_price = table.Column<decimal>(type: "numeric", nullable: true),
                    output_price = table.Column<decimal>(type: "numeric", nullable: true),
                    recommended_roles = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_overrides", x => new { x.provider, x.model_id });
                    table.CheckConstraint("ck_model_overrides_prices", "(input_price IS NULL) = (output_price IS NULL)");
                    table.ForeignKey(
                        name: "fk_model_overrides_models_provider_model_id",
                        columns: x => new { x.provider, x.model_id },
                        principalTable: "models",
                        principalColumns: new[] { "provider", "model_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_role_defaults_model_needs_provider",
                table: "role_defaults",
                sql: "provider IS NOT NULL OR model_id IS NULL");

            // Hiding is an override from now on, and a model's status is its lifecycle only. A model hidden
            // before is Active until the next sync of everyone's keys judges it.
            migrationBuilder.Sql(
                """
                INSERT INTO model_overrides (provider, model_id, hidden, recommended_roles)
                SELECT provider, model_id, true, '{}' FROM models WHERE status = 'Hidden';

                UPDATE models SET status = 'Active' WHERE status = 'Hidden';
                """);
        }
    }
}
