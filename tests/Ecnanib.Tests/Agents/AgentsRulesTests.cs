using Ecnanib.Agents;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Holds <see cref="AgentsRules"/> to the code: what Agents can do with an agent is what the
/// rules list, and what tells that it can do more can see it. Other components hold Agents to
/// what follows from that with tests of their own, which ask the rules.
/// </summary>
public class AgentsRulesTests
{
    [Fact]
    public void The_operations_of_agents_are_the_ones_that_are_listed()
    {
        var operations = AgentsRules.Operations();

        var unlisted = AgentsRules.UnlistedOperations();
        Assert.True(
            unlisted.Count == 0,
            $"These operations of Agents are listed neither in {nameof(AgentsRules)}.{nameof(AgentsRules.SettingsOperations)}, " +
            $"nor in {nameof(AgentsRules.ControlOperations)}, nor in {nameof(AgentsRules.ModeOperations)}: " +
            $"{string.Join(", ", unlisted)}. If one of them starts, pauses, resumes, or stops an agent, or closes a position, " +
            "it is a control: it asks what blocks a start where it starts one (AgentStartBlockTests), and records the action " +
            "in the audit trail (AuditRulesTests). If one switches the mode of an agent, it takes what a switch to Live " +
            "takes (FR-AGT-7), and records the switch in the audit trail. Build what applies, and then list the operation " +
            "with what it does.");

        var listed = AgentsRules.SettingsOperations
            .Concat(AgentsRules.ControlOperations)
            .Concat(AgentsRules.ModeOperations)
            .ToList();

        var gone = listed.Select(operation => operation.Key).Except(operations).Order(StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0, $"These are listed as operations of Agents, and Agents has none of that name: {string.Join(", ", gone)}");

        Assert.All(listed, operation =>
            Assert.False(string.IsNullOrWhiteSpace(operation.Value), $"{operation.Key} does not say what it does."));

        // An operation is in one list, and in no other.
        Assert.Equal(listed.Count, listed.Select(operation => operation.Key).Distinct().Count());
    }

    [Fact]
    public void An_agent_can_run_and_the_controls_are_the_operations_beyond_its_settings()
    {
        // Pinned, so that a control that is added shows in a review as what it is: one more
        // thing that the service does with the money of a user. What other components ask of
        // an agent that can run is asked from here on: their tests find these. Two switch the
        // mode of an agent, and the first of them puts real money at stake. Three are the
        // service's doing, and no action of a user: an agent is halted when it reaches a loss
        // limit, runs again when the day of its daily loss limit is over, and waits for its user.
        // One is an operator's, and acts on the agents of every user: the global halt, which
        // keeps them from opening positions while it is on (FR-OPS-3).
        Assert.Equal(
            [
                "IAgentControls.ClosePositionAsync",
                "IAgentControls.PauseAsync",
                "IAgentControls.ResumeAsync",
                "IAgentControls.StartAsync",
                "IAgentControls.StopAsync",
                "IAgentExposure.ClosePositionAtMarketAsync",
                "IAgentHalts.EndDailyHaltsAsync",
                "IAgentHalts.HaltAsync",
                "IAgentModes.SwitchToDemoAsync",
                "IAgentModes.SwitchToLiveAsync",
                "IAgentTrading.RequireAttentionAsync",
                "IGlobalHaltSwitch.SwitchAsync",
            ],
            AgentsRules.OperationsBeyondSettings());
    }

    [Fact]
    public void What_an_agent_did_is_stored_with_the_mode_it_did_it_in()
    {
        using var model = ModelOnly.Context();

        // An agent is switched between Demo and Live, and what it did before the switch stays
        // what it was: every table with rows of an agent says the mode in each row (FR-BIN-11).
        // The changes of its state do. The models of its roles are settings, and are listed as
        // such. A table of positions, orders, trades, or results that is added later is found
        // here until it says the mode as well.
        var unlabeled = AgentsRules.TablesOfAgentRowsWithoutMode(model.Model, typeof(Agent))
            .Where(table => !AgentsRules.AgentRowsWithoutMode.ContainsKey(table))
            .ToList();

        Assert.True(
            unlabeled.Count == 0,
            $"These tables hold rows of an agent, and no row of them says a mode: {string.Join(", ", unlabeled)}. An agent is " +
            "switched between Demo and Live, so what it did is labeled row by row with the mode it did it in, and Demo " +
            "results are never combined with Live results (FR-BIN-11). Give the table a column \"mode\", written when the row " +
            $"is, or list it in {nameof(AgentsRules)}.{nameof(AgentsRules.AgentRowsWithoutMode)} with why nothing in it is a " +
            "result. See docs/agents.md.");

        Assert.Contains("mode", model.Model.FindEntityType(typeof(AgentStateChange))!.GetProperties().Select(property => property.GetColumnName()));

        // A table that is listed is one of the model, and says why.
        Assert.All(AgentsRules.AgentRowsWithoutMode, listed =>
        {
            Assert.Contains(model.Model.GetEntityTypes(), entity => entity.GetTableName() == listed.Key);
            Assert.False(string.IsNullOrWhiteSpace(listed.Value), $"{listed.Key} does not say why it holds no mode.");
        });
    }

    [Fact]
    public void A_table_of_what_an_agent_did_that_says_no_mode_is_found()
    {
        using var planted = new PlantedContext();

        // Found: the positions of an agent, which point at it and say no mode. Not found: the
        // roles of an agent, which say one, and the orders, which point at no agent.
        Assert.Equal(["agent_positions"], AgentsRules.TablesOfAgentRowsWithoutMode(planted.Model, typeof(Planted.PlantedAgent)));
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
    public void An_agent_uses_the_models_of_its_roles_and_a_model_that_one_uses_is_found()
    {
        using var model = ModelOnly.Context();

        // Pinned, so that another way for an agent to use a model shows in a review as what it
        // is: one more thing that Agents is told about when a key or a model goes, and asks
        // Credentials about before it is stored (KeyModelsTests, ModelDependentsTests,
        // AgentKeyDependentsTests). An agent uses the models that its roles were given, and no
        // other (FR-AGT-5).
        Assert.Equal(["agent_roles.model_id"], AgentsRules.ModelChoices(model.Model));

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
            modelBuilder.Entity<Planted.PlantedAgent>().ToTable("planted_agents");

            // Found: a role of an agent, with the model that it was given.
            modelBuilder.Entity<Planted.PlantedRole>(role =>
            {
                role.ToTable("planted_roles");
                role.Property(row => row.ModelId).HasColumnName("model_id");
                role.HasOne<Planted.PlantedModel>().WithMany().HasForeignKey(row => row.ModelId);

                // A row of an agent that says the mode it is of.
                role.Property(row => row.Mode).HasColumnName("mode");
                role.HasOne<Planted.PlantedAgent>().WithMany().HasForeignKey(row => row.AgentId);
            });

            // Not found: it points at a model, and is of another component.
            modelBuilder.Entity<PlantedElsewhere>(elsewhere =>
            {
                elsewhere.ToTable("planted_elsewhere");
                elsewhere.HasOne<Planted.PlantedModel>().WithMany().HasForeignKey(row => row.ModelId);
            });

            // A row of an agent that says no mode.
            modelBuilder.Entity<Planted.PlantedPosition>(position =>
            {
                position.ToTable("agent_positions");
                position.HasOne<Planted.PlantedAgent>().WithMany().HasForeignKey(row => row.AgentId);
            });
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
