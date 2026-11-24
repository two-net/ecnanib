using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEquitySignal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added by hand: the model has nothing to say this with, so nothing was generated.
            // The equity of an agent, as it is recorded over time, is on the screen of the
            // agent, with its curve and the figures that are worked out from it (FR-RPT-2). So
            // the database says when one is recorded, as it says a change of every other row
            // that a page follows, with the function of AddLiveSignals: the subject, the owner
            // of the row, and the agent. See docs/live-updates.md and docs/agent-screen.md.
            //
            // Only a row that is added is said. A row is never changed, and the rows of an
            // agent go with the agent, which its own table says: one signal for each of them
            // would say nothing more.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT ON equity_snapshots
                FOR EACH ROW EXECUTE FUNCTION live_signal('Equity', 'agent_id');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER live_signal ON equity_snapshots;");
        }
    }
}
