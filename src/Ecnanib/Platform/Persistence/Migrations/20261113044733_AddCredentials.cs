using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    encrypted_value = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_id = table.Column<string>(type: "text", nullable: false),
                    masked_suffix = table.Column<string>(type: "text", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_credentials_ai_providers_provider",
                        column: x => x.provider,
                        principalTable: "ai_providers",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_credentials_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "exchange_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    encrypted_value = table.Column<byte[]>(type: "bytea", nullable: false),
                    master_key_id = table.Column<string>(type: "text", nullable: false),
                    masked_suffix = table.Column<string>(type: "text", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_exchange_credentials_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_credentials_provider",
                table: "ai_credentials",
                column: "provider");

            migrationBuilder.CreateIndex(
                name: "ix_ai_credentials_user_id_provider",
                table: "ai_credentials",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exchange_credentials_user_id_environment",
                table: "exchange_credentials",
                columns: new[] { "user_id", "environment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_credentials");

            migrationBuilder.DropTable(
                name: "exchange_credentials");
        }
    }
}
