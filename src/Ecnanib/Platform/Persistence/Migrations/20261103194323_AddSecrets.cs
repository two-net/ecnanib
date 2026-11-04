using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "master_key_activations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_master_key_activations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_secrets_key_id",
                table: "secrets",
                column: "key_id");
        }
    }
}
