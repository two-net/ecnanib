using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptTemplateVersionGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added by hand: the model has nothing to say this with, so nothing was generated.
            // A version of a prompt template is never changed, and an edit is a new version
            // (FR-PRM-6). The database holds to that itself, whatever asks: a later migration,
            // the application, or a person. Of a row that is stored, only whether it is in use
            // changes, which is how the next version takes its place. No row is deleted, since
            // an agent may have used it. A row is still added freely: that is how a version
            // arrives. See docs/prompts.md.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION prompt_templates_keep_versions() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF (NEW.id, NEW.kind, NEW.subject, NEW.version, NEW.text)
                            IS NOT DISTINCT FROM (OLD.id, OLD.kind, OLD.subject, OLD.version, OLD.text) THEN
                            RETURN NEW;
                        END IF;
                    END IF;

                    RAISE EXCEPTION 'A version of a prompt template is never changed or deleted. To edit a template, add its next version (FR-PRM-6).'
                        USING ERRCODE = 'restrict_violation', HINT = 'See docs/prompts.md.';
                END;
                $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER prompt_templates_keep_versions
                BEFORE UPDATE OR DELETE ON prompt_templates
                FOR EACH ROW EXECUTE FUNCTION prompt_templates_keep_versions();
                """);

            // Emptying the table deletes no row one by one, so it is refused of its own.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER prompt_templates_keep_rows
                BEFORE TRUNCATE ON prompt_templates
                FOR EACH STATEMENT EXECUTE FUNCTION prompt_templates_keep_versions();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER prompt_templates_keep_rows ON prompt_templates;");
            migrationBuilder.Sql("DROP TRIGGER prompt_templates_keep_versions ON prompt_templates;");
            migrationBuilder.Sql("DROP FUNCTION prompt_templates_keep_versions();");
        }
    }
}
