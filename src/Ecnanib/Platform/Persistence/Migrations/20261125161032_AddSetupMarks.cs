using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddSetupMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "decisions_reviewed_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "live_considered_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Changed by hand: the statement below. From here on, a switch of an agent to Live
            // stores that its user considered Live, with the switch (FR-UI-4). An account that
            // switched one before this is applied did so too: it is given the time of its first
            // switch, as the audit trail holds it. An account that never switched stays without
            // a time, and so does every account for the decisions: nothing says that they were
            // reviewed. See docs/dashboard.md.
            migrationBuilder.Sql(
                """
                UPDATE users
                SET live_considered_at = switched.first_at
                FROM (
                    SELECT user_id, MIN(occurred_at) AS first_at
                    FROM audit_events
                    WHERE action = 'AgentSwitchedToLive'
                    GROUP BY user_id
                ) AS switched
                WHERE switched.user_id = users.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "decisions_reviewed_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "live_considered_at",
                table: "users");
        }
    }
}
