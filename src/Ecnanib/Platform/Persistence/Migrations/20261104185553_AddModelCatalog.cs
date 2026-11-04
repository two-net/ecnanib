using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_sync_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    keys = table.Column<int>(type: "integer", nullable: false),
                    keys_failed = table.Column<int>(type: "integer", nullable: false),
                    models_added = table.Column<int>(type: "integer", nullable: false),
                    models_updated = table.Column<int>(type: "integer", nullable: false),
                    models_deprecated = table.Column<int>(type: "integer", nullable: false),
                    models_retired = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_sync_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    family = table.Column<string>(type: "text", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    context_window = table.Column<int>(type: "integer", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    image_input = table.Column<bool>(type: "boolean", nullable: true),
                    structured_output = table.Column<bool>(type: "boolean", nullable: true),
                    tool_use = table.Column<bool>(type: "boolean", nullable: true),
                    live_search = table.Column<bool>(type: "boolean", nullable: true),
                    reasoning_effort = table.Column<bool>(type: "boolean", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_models", x => new { x.provider, x.model_id });
                    table.ForeignKey(
                        name: "fk_models_providers_provider",
                        column: x => x.provider,
                        principalTable: "providers",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_availability",
                columns: table => new
                {
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    available = table.Column<bool>(type: "boolean", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_availability", x => new { x.credential_id, x.provider, x.model_id });
                    table.ForeignKey(
                        name: "fk_model_availability_models_provider_model_id",
                        columns: x => new { x.provider, x.model_id },
                        principalTable: "models",
                        principalColumns: new[] { "provider", "model_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_sync_runs_provider_started_at",
                table: "catalog_sync_runs",
                columns: new[] { "provider", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_model_availability_provider_model_id",
                table: "model_availability",
                columns: new[] { "provider", "model_id" });

            migrationBuilder.CreateIndex(
                name: "ix_model_availability_user_id",
                table: "model_availability",
                column: "user_id");
        }
    }
}
