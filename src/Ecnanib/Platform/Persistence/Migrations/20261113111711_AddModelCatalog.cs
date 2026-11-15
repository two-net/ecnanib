using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    identifier = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    context_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deprecated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    missed_syncs = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_models", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_models_ai_providers_provider",
                        column: x => x.provider,
                        principalTable: "ai_providers",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    cause = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    failure = table.Column<string>(type: "text", nullable: true),
                    added = table.Column<int>(type: "integer", nullable: false),
                    changed = table.Column<int>(type: "integer", nullable: false),
                    unavailable = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_sync_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_sync_runs_ai_providers_provider",
                        column: x => x.provider,
                        principalTable: "ai_providers",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_models_provider_identifier",
                table: "ai_models",
                columns: new[] { "provider", "identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_sync_runs_provider_started_at",
                table: "catalog_sync_runs",
                columns: new[] { "provider", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_models");

            migrationBuilder.DropTable(
                name: "catalog_sync_runs");
        }
    }
}
