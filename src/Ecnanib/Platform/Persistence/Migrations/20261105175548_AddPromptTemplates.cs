using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddPromptTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prompt_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    market = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    timeframe = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_templates", x => x.id);
                    table.CheckConstraint("ck_prompt_templates_layer_dimension", "(layer NOT IN ('Role', 'OutputContract') OR role IS NOT NULL) AND (layer <> 'Market' OR market IS NOT NULL) AND (layer <> 'Timeframe' OR timeframe IS NOT NULL)");
                    table.CheckConstraint("ck_prompt_templates_version", "version >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_layer_role_market_timeframe_version",
                table: "prompt_templates",
                columns: new[] { "layer", "role", "market", "timeframe", "version" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
