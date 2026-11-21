using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Holds <see cref="PromptRules"/> to the code: templates are managed by the system and
/// delivered as seed data (FR-PRM-7), each role output records the template versions it used
/// (FR-PRM-6), and what an AI provider is sent is limited to market data, agent settings,
/// position state, and lessons (NFR-SEC-8), with nothing that says whose it is (FR-PRM-9).
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

        // The rule finds the table that holds what a role answered, so it would find another.
        Assert.Equal(["role_outputs"], PromptRules.TablesOfRoleOutputs(model.Model));

        // Its rows say which versions of the templates the call used: a version is never
        // changed, so a row says for good what the model was told.
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

    [Fact]
    public void What_composes_a_prompt_is_given_no_template_and_one_market_and_one_timeframe()
    {
        var given = PromptRules.GivenTo(typeof(IPromptComposition));

        // The rule looks into what is handed over: it finds the context of the agent, and the
        // limits inside it.
        Assert.Contains(given, part => part.What == typeof(AgentContext));
        Assert.Contains(given, part => part.What == typeof(AgentRisk));

        var templates = given.Where(part => PromptRules.Templates.Contains(part.What)).Select(part => part.Where).ToList();

        Assert.True(
            templates.Count == 0,
            $"What composes a prompt is handed a template, or told which one: {string.Join(", ", templates)}. The modules of a " +
            "prompt are selected from the agent's settings, and a mismatched combination cannot be chosen (FR-PRM-4): " +
            "IPromptComposition reads the templates itself, by the market and the timeframe of the context. See docs/prompts.md.");

        // The market and the timeframe are said once for a prompt, by the context of the agent.
        // A second place could say another, and the modules would follow one of the two. The
        // prompt of a lesson says them once as well, by the closed position, and has no module.
        Assert.Equal(
            ["ComposeAsync(parts).Agent.Market", "ComposeLessonAsync(parts).Position.Market"],
            given.Where(part => part.What == typeof(Market)).Select(part => part.Where).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["ComposeAsync(parts).Agent.Timeframe", "ComposeLessonAsync(parts).Position.Timeframe"],
            given.Where(part => part.What == typeof(Timeframe)).Select(part => part.Where).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_composition_that_is_handed_a_template_or_a_second_market_is_found()
    {
        var given = PromptRules.GivenTo(typeof(IPlantedComposition));

        // Found: the modules that a caller chose, in a list and as the templates of an agent,
        // and what a template is of inside them.
        Assert.Equal(
            [
                "ComposeAsync(parts).Chosen",
                "ComposeAsync(parts).Chosen.Lesson",
                "ComposeAsync(parts).Chosen.Lesson.Kind",
                "ComposeAsync(parts).Chosen.Market",
                "ComposeAsync(parts).Chosen.Market.Kind",
                "ComposeAsync(parts).Chosen.Profile",
                "ComposeAsync(parts).Chosen.Profile.Kind",
                "ComposeAsync(parts).Chosen.Roles[]",
                "ComposeAsync(parts).Chosen.Roles[].Kind",
                "ComposeAsync(parts).Chosen.Timeframe",
                "ComposeAsync(parts).Chosen.Timeframe.Kind",
                "ComposeAsync(parts).Modules[]",
                "ComposeAsync(parts).Modules[].Kind",
            ],
            given.Where(part => PromptRules.Templates.Contains(part.What)).Select(part => part.Where).Order(StringComparer.Ordinal));

        // Found: the market that is said beside the one of the context.
        Assert.Equal(
            ["ComposeAsync(market)", "ComposeAsync(parts).Agent.Market"],
            given.Where(part => part.What == typeof(Market)).Select(part => part.Where).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void What_a_provider_is_sent_is_market_data_agent_settings_position_state_and_lessons()
    {
        // One operation composes the prompt of a role call, from the role, the parts, and
        // nothing else. And one composes the prompt of the lesson of a closed position, from
        // its parts and nothing else (FR-EXP-6). Pinned: a third would be a third thing that a
        // provider is sent.
        var operations = typeof(IPromptComposition).GetMethods().ToDictionary(
            operation => operation.Name, operation => operation.GetParameters().Select(parameter => parameter.Name).ToList());

        Assert.Equal(["ComposeAsync", "ComposeLessonAsync"], operations.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["role", "parts", "cancellationToken"], operations["ComposeAsync"]);
        Assert.Equal(["parts", "cancellationToken"], operations["ComposeLessonAsync"]);

        // Every value that is handed over is listed with its kind, and none is an identifier
        // or a text that does not say what it holds.
        var violations = PromptRules.FindUnlisted(typeof(IPromptComposition));

        Assert.True(
            violations.Count == 0,
            "What composes a prompt is handed what an AI provider is sent. Data sent to AI providers is limited to market " +
            "data, agent settings, position state, and lessons (NFR-SEC-8), and a prompt holds no secret, no email address, " +
            "no account identifier, and no other personal data (FR-PRM-9). See docs/prompts.md:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));

        // The four kinds of the requirement, and each of them is sent.
        Assert.Equal(["MarketData", "AgentSettings", "PositionState", "Lessons"], Enum.GetNames<SentData>());
        Assert.Equal(Enum.GetValues<SentData>(), PromptRules.Sent.Values.Distinct().Order());
    }

    [Fact]
    public void The_texts_that_a_provider_is_sent_are_the_symbol_its_asset_the_data_package_the_lessons_and_what_opened_a_closed_position()
    {
        // Pinned, so that a change to the list shows in a review as what it is: a decision
        // about what a provider is sent that could say who a user is.
        Assert.Equal(
            [
                "ComposeAsync(parts).Agent.QuoteAsset",
                "ComposeAsync(parts).Agent.Symbol",
                "ComposeAsync(parts).DataPackage",
                "ComposeAsync(parts).Lessons[]",
                "ComposeLessonAsync(parts).Plan",
                "ComposeLessonAsync(parts).Position.QuoteAsset",
                "ComposeLessonAsync(parts).Position.Symbol",
                "ComposeLessonAsync(parts).Review",
            ],
            PromptRules.FreeText.Keys.Order(StringComparer.Ordinal));
        Assert.All(PromptRules.FreeText, text => Assert.False(string.IsNullOrWhiteSpace(text.Value), $"{text.Key} does not say what it holds."));

        // The market data is the data package, and the lessons are the lessons: everything
        // else that is sent is a setting of the agent, or the state of its position. For the
        // lesson of a closed position, it is that position, with the plan that opened it and
        // the review of that plan.
        Assert.Equal(["ComposeAsync(parts).DataPackage"], Listed(SentData.MarketData));
        Assert.Equal(["ComposeAsync(parts).Lessons[]"], Listed(SentData.Lessons));
        Assert.All(Listed(SentData.AgentSettings).Concat(Listed(SentData.PositionState)), sent =>
            Assert.True(
                sent.StartsWith("ComposeAsync(parts).Agent.", StringComparison.Ordinal)
                || sent.StartsWith("ComposeLessonAsync(parts).Position.", StringComparison.Ordinal)
                || sent is "ComposeLessonAsync(parts).Plan" or "ComposeLessonAsync(parts).Review",
                $"{sent} is listed as a setting of an agent or as the state of a position, and is neither of the context of an agent nor of a closed position."));

        // No id of the agent, of its user, or of anything else is among them, and no name.
        var given = PromptRules.DataGivenTo(typeof(IPromptComposition));

        Assert.DoesNotContain(given, value => value.What == typeof(Guid));
        Assert.DoesNotContain(given, value => value.Where.Contains("Name", StringComparison.Ordinal));

        static IEnumerable<string> Listed(SentData kind) =>
            PromptRules.Sent.Where(sent => sent.Value == kind).Select(sent => sent.Key).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void A_composition_that_is_handed_a_name_or_an_identifier_is_found()
    {
        const string NotListed =
            "is handed over, and is not listed. List it in PromptRules.Sent as market data, agent settings, position state, " +
            "or lessons, or do not hand it over.";
        const string Identifier = "is an identifier. A prompt holds no account identifier (FR-PRM-9).";
        const string Text = "is text, and does not say what it holds. List it in PromptRules.FreeText.";

        // Found: the id of the agent and the id of its user, which are identifiers, and the
        // name of the agent, which is text that nothing vouches for. Not found: the context,
        // the data package, and the lessons, which are listed.
        Assert.Equal(
            [
                $"ComposeAsync(parts).AgentId: {Identifier}",
                $"ComposeAsync(parts).AgentId: {NotListed}",
                $"ComposeAsync(parts).AgentName: {NotListed}",
                $"ComposeAsync(parts).AgentName: {Text}",
                $"ComposeAsync(userId): {Identifier}",
                $"ComposeAsync(userId): {NotListed}",
            ],
            PromptRules.FindUnlisted(typeof(IPlantedNamingComposition)));
    }

    [Fact]
    public void Only_the_Decision_Engine_calls_a_model()
    {
        // The rule finds the calls that the tests make, so it would find one of the application.
        Assert.NotEmpty(PromptRules.CallersOfModels(typeof(PromptRulesTests).Assembly));

        var others = PromptRules.CallersOfModels(typeof(Program).Assembly)
            .Where(caller => !caller.StartsWith(PromptRules.CallsModels, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            others.Count == 0,
            $"These call a model, or put together what a call is made with, and are not of the Decision Engine: {string.Join(", ", others)}. " +
            "Data sent to AI providers is limited to market data, agent settings, position state, and lessons (NFR-SEC-8). " +
            "What holds a call to that is the prompt, which IPromptComposition composes and screens: so a model is called by " +
            "the Decision Engine alone, with a prompt that it composed. See docs/prompts.md.");
    }

    /// <summary>
    /// A composition whose caller says whose agent the prompt is for, and what the agent is
    /// called. The prompt of a lesson is as the application composes it, so that only what is
    /// planted is found.
    /// </summary>
    private interface IPlantedNamingComposition
    {
        Task<string> ComposeAsync(Role role, PlantedNamingParts parts, Guid userId, CancellationToken cancellationToken);

        Task<string> ComposeLessonAsync(LessonParts parts, CancellationToken cancellationToken);
    }

    private sealed record PlantedNamingParts(
        AgentContext Agent, string DataPackage, IReadOnlyList<string> Lessons, string AgentName, Guid AgentId);

    /// <summary>A composition whose caller chooses the modules, and says the market twice.</summary>
    private interface IPlantedComposition
    {
        Task<string> ComposeAsync(Role role, PlantedParts parts, Market market, CancellationToken cancellationToken);
    }

    private sealed record PlantedParts(AgentContext Agent, IReadOnlyList<PromptTemplate> Modules, TemplatesInUse? Chosen);

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
