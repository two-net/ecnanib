using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <summary>
    /// NFR-COST-01, AI-04: the calls the council made for a role and the reasoning share of its output tokens move from
    /// the transcript JSON, which retention sweeps, onto the permanent role output row. Rows whose transcript still
    /// exists take the values from it; a called role whose transcript is gone counts as one call when it reported tokens
    /// or latency. A historic call that threw or timed out before any answer cannot be told from a role that was never
    /// called once its transcript is gone, and stays at 0.
    /// </summary>
    public partial class AddRoleOutputAttempts : Migration
    {
        /// <summary>The backfill, exposed so the live tests can run it over seeded rows.</summary>
        internal const string BackfillSql =
            """
            UPDATE decision_role_outputs r SET
                attempts = GREATEST(
                    COALESCE((SELECT (t.output->>'attempts')::int FROM deliberation_transcripts t
                              WHERE t.decision_id = r.decision_id AND t.role = r.role AND jsonb_typeof(t.output->'attempts') = 'number'), 0),
                    CASE WHEN r.status NOT IN ('Disabled', 'Skipped') AND (r.input_tokens > 0 OR r.output_tokens > 0 OR r.latency_ms > 0) THEN 1 ELSE 0 END),
                reasoning_tokens = COALESCE((SELECT (t.output->>'reasoningTokens')::int FROM deliberation_transcripts t
                                             WHERE t.decision_id = r.decision_id AND t.role = r.role AND jsonb_typeof(t.output->'reasoningTokens') = 'number'), 0);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "decision_role_outputs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_tokens",
                table: "decision_role_outputs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
