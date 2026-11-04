using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    key_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_four = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    spot_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    futures_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    ip_restricted = table.Column<bool>(type: "boolean", nullable: true),
                    exchange_uid = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_user_id",
                table: "credentials",
                column: "user_id");
        }
    }
}
