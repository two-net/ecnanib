using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <inheritdoc />
    public partial class ExtendExperienceMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The metrics JSON now carries the counts and sums its ratios derive from (FR-EXP-04); rows written in the
            // old shape lack them, and the experience refresher rebuilds every row on its next run.
            migrationBuilder.Sql("DELETE FROM experience_stats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
