using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <summary>
    /// FR-KEY-04, A-02: the users' API keys move from <c>text</c> to <c>bytea</c> so the database module can hold them
    /// encrypted. PostgreSQL has no assignment cast from text to bytea, so the scaffolded <c>AlterColumn</c> (an
    /// <c>ALTER COLUMN … TYPE bytea</c> without <c>USING</c>) fails with 42804 on every database, rows or not; the
    /// hand-written statements keep each value as its UTF-8 bytes. The new <c>cipher</c> column says what a row holds:
    /// <c>Plain</c> for these converted rows, which the start-up step encrypts before any job runs, <c>AesGcm</c> once
    /// the application wrote it.
    /// </summary>
    public partial class ProtectStoredKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE provider_keys ALTER COLUMN api_key TYPE bytea USING convert_to(api_key, 'UTF8')");

            migrationBuilder.AddColumn<string>(
                name: "cipher",
                table: "provider_keys",
                type: "text",
                nullable: false,
                defaultValue: "Plain");

            migrationBuilder.Sql("ALTER TABLE exchange_keys ALTER COLUMN api_key TYPE bytea USING convert_to(api_key, 'UTF8'), ALTER COLUMN api_secret TYPE bytea USING convert_to(api_secret, 'UTF8')");

            migrationBuilder.AddColumn<string>(
                name: "cipher",
                table: "exchange_keys",
                type: "text",
                nullable: false,
                defaultValue: "Plain");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
