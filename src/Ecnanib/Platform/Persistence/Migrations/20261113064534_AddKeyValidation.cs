using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "futures_trading",
                table: "exchange_credentials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ip_restricted",
                table: "exchange_credentials",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "spot_trading",
                table: "exchange_credentials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The default is set by hand. The generated one was the empty text, which is no
            // status. A credential that is stored when this is applied was never checked, and
            // says so: the job that checks the keys takes it with its next run, and deletes it
            // if Binance reports that it can withdraw. See docs/credentials.md.
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "exchange_credentials",
                type: "text",
                nullable: false,
                defaultValue: "Unchecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "validated_at",
                table: "exchange_credentials",
                type: "timestamp with time zone",
                nullable: true);

            // The default is set by hand, as for the credentials of Binance above.
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "ai_credentials",
                type: "text",
                nullable: false,
                defaultValue: "Unchecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "validated_at",
                table: "ai_credentials",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "futures_trading",
                table: "exchange_credentials");

            migrationBuilder.DropColumn(
                name: "ip_restricted",
                table: "exchange_credentials");

            migrationBuilder.DropColumn(
                name: "spot_trading",
                table: "exchange_credentials");

            migrationBuilder.DropColumn(
                name: "status",
                table: "exchange_credentials");

            migrationBuilder.DropColumn(
                name: "validated_at",
                table: "exchange_credentials");

            migrationBuilder.DropColumn(
                name: "status",
                table: "ai_credentials");

            migrationBuilder.DropColumn(
                name: "validated_at",
                table: "ai_credentials");
        }
    }
}
