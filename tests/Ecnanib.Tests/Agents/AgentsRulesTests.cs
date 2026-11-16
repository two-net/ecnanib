using Ecnanib.Agents;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Holds <see cref="AgentsRules"/> to the code: what Agents can do with an agent is what the
/// rules list, and what tells that it can do more can see it. Other components wait for that
/// with tests of their own, which ask the rules.
/// </summary>
public class AgentsRulesTests
{
    [Fact]
    public void The_operations_of_agents_are_the_ones_that_are_listed()
    {
        var operations = AgentsRules.Operations();

        var unlisted = AgentsRules.OperationsBeyondSettings();
        Assert.True(
            unlisted.Count == 0,
            $"These operations of Agents are not listed in {nameof(AgentsRules)}.{nameof(AgentsRules.SettingsOperations)}: " +
            $"{string.Join(", ", unlisted)}. Agents kept settings and nothing else, and other components wait for what it does " +
            "next. If one of these starts, pauses, resumes, or stops an agent, switches its mode, or closes a position, the " +
            "tests that wait for it fail now as well, each with what to build: asking what blocks a start (AgentStartBlockTests), " +
            "being told when a key goes (KeyDependentsTests), and recording the action in the audit trail (AuditRulesTests). " +
            "Build what they ask for, or say there what still waits, and then list the operation with what it does.");

        var gone = AgentsRules.SettingsOperations.Keys.Except(operations).Order(StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0, $"These are listed as operations of Agents, and Agents has none of that name: {string.Join(", ", gone)}");

        Assert.All(AgentsRules.SettingsOperations, operation =>
            Assert.False(string.IsNullOrWhiteSpace(operation.Value), $"{operation.Key} does not say what it does."));
    }

    [Fact]
    public void An_operation_that_is_added_to_a_public_interface_is_found()
    {
        // The methods of the public interfaces of a namespace, and of the ones nested in it.
        // What is not public is not an operation: nothing outside the component can call it.
        Assert.Equal(
            ["IPlantedControls.PauseAsync", "IPlantedControls.StartAsync", "IPlantedReads.ListAsync"],
            AgentsRules.OperationsOf(typeof(AgentsRulesTests).Assembly, "Ecnanib.Tests.Agents.Planted"));
    }

    [Fact]
    public void No_agent_uses_a_model_yet_and_a_model_that_one_uses_is_found()
    {
        using var model = ModelOnly.Context();

        // Agents has a table of its own, and none of its rows points at a model of the catalog.
        Assert.Contains(model.Model.GetEntityTypes(), entity => AgentsRules.IsOfAgents(entity.ClrType));
        Assert.Empty(AgentsRules.ModelChoices(model.Model));

        // In a model where a row of the component does, it is found, by its table and its column.
        using var planted = new PlantedContext();

        Assert.Equal(
            ["planted_roles.model_id"],
            AgentsRules.ModelChoices(planted.Model, "Ecnanib.Tests.Agents.Planted", typeof(Planted.PlantedModel)));
    }

    [Fact]
    public void Once_positions_or_orders_are_stored_what_keeps_them_says_what_an_agent_holds()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Positions and orders are not built yet, and nothing answers for them: no agent holds
        // any. As soon as a table holds them, whatever keeps it must: Agents cannot look at
        // positions itself, and would let the market of an agent be changed under an open one.
        var stored = AgentsRules.TablesOfPositionsOrOrders(model.Model);
        var answers = application.Services.GetServices<IAgentExposure>().Any();

        Assert.True(
            answers || stored.Count == 0,
            $"Positions or orders are stored ({string.Join(", ", stored)}), and nothing says whether an agent has an open " +
            "position or open orders. The market, the symbol, and the timeframe of a stopped agent change only while it has " +
            $"none, and it is deleted only then (FR-AGT-8). Implement {nameof(IAgentExposure)} in the component that keeps " +
            "them, and register it. See docs/agents.md.");
    }

    [Fact]
    public void A_table_of_positions_or_of_orders_is_found_by_its_name()
    {
        using var planted = new PlantedContext();

        Assert.Equal(["agent_positions", "orders"], AgentsRules.TablesOfPositionsOrOrders(planted.Model));
    }

    /// <summary>A model with a row of a component that points at a model, and with tables of positions and orders.</summary>
    private sealed class PlantedContext() : DbContext(new DbContextOptionsBuilder().UseNpgsql().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Planted.PlantedModel>().ToTable("planted_models");

            // Found: a role of an agent, with the model that it was given.
            modelBuilder.Entity<Planted.PlantedRole>(role =>
            {
                role.ToTable("planted_roles");
                role.Property(row => row.ModelId).HasColumnName("model_id");
                role.HasOne<Planted.PlantedModel>().WithMany().HasForeignKey(row => row.ModelId);
            });

            // Not found: it points at a model, and is of another component.
            modelBuilder.Entity<PlantedElsewhere>(elsewhere =>
            {
                elsewhere.ToTable("planted_elsewhere");
                elsewhere.HasOne<Planted.PlantedModel>().WithMany().HasForeignKey(row => row.ModelId);
            });

            modelBuilder.Entity<Planted.PlantedPosition>().ToTable("agent_positions");
            modelBuilder.Entity<Planted.PlantedOrder>().ToTable("orders");

            // Not found: neither word is in its name, though "order" is in "border".
            modelBuilder.Entity<Planted.PlantedBorder>().ToTable("borders");
        }
    }

    private sealed class PlantedElsewhere
    {
        public Guid Id { get; set; }

        public Guid ModelId { get; set; }
    }
}
