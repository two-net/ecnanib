using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A notification that is stored when this is applied is sent by no email: its user
            // had not opted in when it was raised, because nobody could (FR-NTF-3). See "Email
            // for critical events" in docs/notifications.md.
            migrationBuilder.AddColumn<bool>(
                name: "email_pending",
                table: "notifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "emailed_at",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "email_opt_ins",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opted_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_opt_ins", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_email_opt_ins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                table: "notifications",
                column: "created_at",
                filter: "email_pending");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id",
                table: "notifications",
                column: "user_id",
                filter: "NOT is_read");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_created_at",
                table: "notifications",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_opt_ins");

            migrationBuilder.DropIndex(
                name: "ix_notifications_created_at",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notifications_user_id",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notifications_user_id_created_at",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "email_pending",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "emailed_at",
                table: "notifications");
        }
    }
}
