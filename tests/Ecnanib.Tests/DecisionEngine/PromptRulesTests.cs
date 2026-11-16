using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Holds <see cref="PromptRules"/> to the code: templates are managed by the system and
/// delivered as seed data (FR-PRM-7), and each role output records the template versions it
/// used (FR-PRM-6).
/// </summary>
public class PromptRulesTests
{
    [Fact]
    public void Only_the_seed_creates_a_template()
    {
        var creators = PromptRules.CreatorsOfTemplates(typeof(Program).Assembly);

        // The rule finds the seed itself, so it would find another that creates a template.
        Assert.NotEmpty(creators);

        var others = creators.Where(creator => !creator.StartsWith(PromptRules.Seed, StringComparison.Ordinal)).ToList();

        Assert.True(
            others.Count == 0,
            $"These create a prompt template, and are not its seed: {string.Join(", ", others)}. Templates are managed by the " +
            "system and delivered as seed data (FR-PRM-7, NFR-DAT-10): a template arrives with a migration that was generated " +
            "from the rows of PromptTemplateConfiguration, and no user and no part of the application stores one. To edit a " +
            "template, add its next version there. See docs/prompts.md.");
    }

    [Fact]
    public void Once_role_outputs_are_stored_each_records_the_template_versions_it_used()
    {
        using var model = ModelOnly.Context();

        // Role outputs are not built, so no table holds them and nothing is found. As soon as
        // one does, its rows must say which versions of the templates the call used: a version
        // is never changed, so the row then says for good what the model was told.
        var without = PromptRules.RoleOutputTablesWithoutTemplates(model.Model, typeof(PromptTemplate));

        Assert.True(
            without.Count == 0,
            $"These tables hold what a role answered, and no row of them names a prompt template: {string.Join(", ", without)}. " +
            "Each role output records the template versions it used (FR-PRM-6, FR-TEAM-8). Give the table a foreign key to " +
            "prompt_templates for each template of the call, which IPromptTemplates hands out as TemplatesInUse.For(role), or " +
            "give it a table of its own that points at both. Declare the key with OnDelete(DeleteBehavior.Restrict), as every " +
            "key to a seeded table is. See docs/prompts.md.");
    }

    [Fact]
    public void A_table_of_role_outputs_that_names_no_template_is_found()
    {
        using var planted = new PlantedContext();

        // By their names: what a role answered, and a call of a role. Not the roles themselves,
        // and not an output of something else.
        Assert.Equal(
            ["checked_role_outputs", "role_call_templates", "role_calls", "role_outputs"],
            PromptRules.TablesOfRoleOutputs(planted.Model));

        // Found: the one that names no template. Not found: the one that points at a template
        // itself, and the one that a table of its own points at a template for.
        Assert.Equal(["role_outputs"], PromptRules.RoleOutputTablesWithoutTemplates(planted.Model, typeof(PlantedTemplate)));
    }

    /// <summary>A model with tables of role outputs that name a template, and one that names none.</summary>
    private sealed class PlantedContext() : DbContext(new DbContextOptionsBuilder().UseNpgsql().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PlantedTemplate>().ToTable("planted_templates");

            // Found: what a role answered, with nothing that says what it was told.
            modelBuilder.Entity<PlantedOutput>().ToTable("role_outputs");

            // Not found: it points at the template that it used.
            modelBuilder.Entity<PlantedCheckedOutput>(output =>
            {
                output.ToTable("checked_role_outputs");
                output.HasOne<PlantedTemplate>().WithMany().HasForeignKey(row => row.TemplateId);
            });

            // Not found: a table of its own says which templates a call used.
            modelBuilder.Entity<PlantedCall>().ToTable("role_calls");
            modelBuilder.Entity<PlantedCallTemplate>(used =>
            {
                used.ToTable("role_call_templates");
                used.HasOne<PlantedCall>().WithMany().HasForeignKey(row => row.CallId);
                used.HasOne<PlantedTemplate>().WithMany().HasForeignKey(row => row.TemplateId);
            });

            // No tables of role outputs: the roles, and what is put out by something else.
            modelBuilder.Entity<PlantedRole>().ToTable("roles");
            modelBuilder.Entity<PlantedExport>().ToTable("export_outputs");
        }
    }

    private sealed class PlantedTemplate
    {
        public int Id { get; set; }
    }

    private sealed class PlantedOutput
    {
        public Guid Id { get; set; }
    }

    private sealed class PlantedCheckedOutput
    {
        public Guid Id { get; set; }

        public int TemplateId { get; set; }
    }

    private sealed class PlantedCall
    {
        public Guid Id { get; set; }
    }

    private sealed class PlantedCallTemplate
    {
        public Guid Id { get; set; }

        public Guid CallId { get; set; }

        public int TemplateId { get; set; }
    }

    private sealed class PlantedRole
    {
        public Guid Id { get; set; }
    }

    private sealed class PlantedExport
    {
        public Guid Id { get; set; }
    }
}
