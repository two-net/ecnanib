using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added by hand: the model has nothing to say this with, so nothing was generated.
            // The database says that a row of a user changed, whatever wrote it, so that the
            // open pages of that user show the change without a manual refresh (FR-NTF-2). It
            // says so on the channel that one listener of each running instance listens to,
            // when the change is committed, and not for a transaction that is rolled back. What
            // it says is the subject, the owner of the row, and the agent that the row is
            // about, and nothing of what changed: that is asked for, by owner. See
            // docs/live-updates.md.
            //
            // A trigger names its subject, which is a member of LiveSubject, then the column
            // that says the agent, or nothing where the rows are about none, and then the
            // columns whose change alone is said to nobody: what is written again and again
            // with nothing to show for it on a page.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION live_signal() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    changed jsonb;
                    routine text[] := COALESCE(TG_ARGV[2:], '{}');
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        changed := to_jsonb(OLD);
                    ELSE
                        changed := to_jsonb(NEW);

                        IF TG_OP = 'UPDATE' AND (to_jsonb(OLD) - routine) IS NOT DISTINCT FROM (changed - routine) THEN
                            RETURN NULL;
                        END IF;
                    END IF;

                    PERFORM pg_notify(
                        'ecnanib_live',
                        TG_ARGV[0] || ' ' || (changed ->> 'user_id')
                            || COALESCE(' ' || (changed ->> NULLIF(TG_ARGV[1], '')), ''));

                    RETURN NULL;
                END;
                $$;
                """);

            // An agent: it was created or deleted, or its state, its mode, or a setting of it
            // changed. When the service last gave a role of it another model is on no page that
            // follows.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT OR UPDATE OR DELETE ON agents
                FOR EACH ROW EXECUTE FUNCTION live_signal('Agent', 'id', 'roles_reassigned_at');
                """);

            // What an agent holds: its positions and its orders. That its user was told of a
            // position, when its funding is read next, and when an order was last asked about
            // show on no page.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT OR UPDATE OR DELETE ON positions
                FOR EACH ROW EXECUTE FUNCTION live_signal('Holdings', 'agent_id', 'alerted_at', 'funding_due_at');
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT OR UPDATE OR DELETE ON orders
                FOR EACH ROW EXECUTE FUNCTION live_signal('Holdings', 'agent_id', 'checked_at');
                """);

            // A cycle that was recorded.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT OR UPDATE OR DELETE ON decision_cycles
                FOR EACH ROW EXECUTE FUNCTION live_signal('Cycle', 'agent_id');
                """);

            // A notification that was raised, read, or removed. What became of its email shows
            // on no page.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER INSERT OR UPDATE OR DELETE ON notifications
                FOR EACH ROW EXECUTE FUNCTION live_signal('Notification', 'agent_id', 'email_pending', 'emailed_at');
                """);

            // A session that ended, however it ended. One that is used is written to with every
            // minute of use, which is said to nobody.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER live_signal
                AFTER DELETE ON sessions
                FOR EACH ROW EXECUTE FUNCTION live_signal('Session');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER live_signal ON sessions;");
            migrationBuilder.Sql("DROP TRIGGER live_signal ON notifications;");
            migrationBuilder.Sql("DROP TRIGGER live_signal ON decision_cycles;");
            migrationBuilder.Sql("DROP TRIGGER live_signal ON orders;");
            migrationBuilder.Sql("DROP TRIGGER live_signal ON positions;");
            migrationBuilder.Sql("DROP TRIGGER live_signal ON agents;");
            migrationBuilder.Sql("DROP FUNCTION live_signal();");
        }
    }
}
