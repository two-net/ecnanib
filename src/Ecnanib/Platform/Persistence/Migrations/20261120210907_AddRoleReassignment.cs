using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleReassignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An agent that is stored when this is applied has the automatic reassignment of its
            // roles off, as a new agent has: its user opts in (FR-TEAM-10). And the service has
            // reassigned no role of it yet. See "Reassigning the roles automatically" in
            // docs/agents.md.
            migrationBuilder.AddColumn<bool>(
                name: "reassigns_roles",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "roles_reassigned_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reassigns_roles",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "roles_reassigned_at",
                table: "agents");
        }
    }
}
