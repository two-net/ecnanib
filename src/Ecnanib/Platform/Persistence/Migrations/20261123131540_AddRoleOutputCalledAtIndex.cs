using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleOutputCalledAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_role_outputs_user_id_called_at",
                table: "role_outputs",
                columns: new[] { "user_id", "called_at" })
                .Annotation("Npgsql:IndexInclude", new[] { "cycle_id", "model_id", "outcome", "input_tokens", "output_tokens", "live_searches" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_role_outputs_user_id_called_at",
                table: "role_outputs");
        }
    }
}
