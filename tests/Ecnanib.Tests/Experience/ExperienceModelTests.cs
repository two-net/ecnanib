using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// What Experience stores hangs on the cycle that it is of, by a key that the Decision Engine
/// declares, because Experience may not use it. And shadow trades never place orders (FR-EXP-5):
/// nothing of Experience can reach the orders of the Exchange Gateway. See "The data" in
/// docs/experience.md, and "The model" in docs/persistence.md. These tests read the model and
/// the architecture rules, and need no database.
/// </summary>
public class ExperienceModelTests
{
    [Theory]
    [InlineData(typeof(AnalystCall), "analyst_calls", "pk_analyst_calls", "fk_analyst_calls_decision_cycles_cycle_id_user_id")]
    [InlineData(typeof(ShadowTrade), "shadow_trades", "pk_shadow_trades", "fk_shadow_trades_decision_cycles_cycle_id_user_id")]
    public void The_keys_to_the_cycle_are_named_for_their_tables_and_the_key_of_the_cycles_keeps_its_name(
        Type entity, string table, string primaryKey, string keyToCycle)
    {
        using var context = ModelOnly.Context();
        var mapped = context.Model.FindEntityType(entity)!;

        // The entity has two configurations, its own in Experience and that of its key to the
        // cycle in the Decision Engine, which is applied first. Both are of the one table, and
        // its keys are named for it, and not for what the entity was called before it had a table.
        Assert.Equal(table, mapped.GetTableName());
        Assert.Equal(primaryKey, mapped.FindPrimaryKey()!.GetName());

        var toCycle = Assert.Single(mapped.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(DecisionCycle));

        Assert.Equal(keyToCycle, toCycle.GetConstraintName());
        Assert.Equal(["cycle_id", "user_id"], toCycle.Properties.Select(property => property.GetColumnName()));

        // It carries the owner along, and the row goes with its cycle (NFR-DAT-9, FR-ACC-9).
        Assert.Equal(["id", "user_id"], toCycle.PrincipalKey.Properties.Select(property => property.GetColumnName()));
        Assert.Equal(DeleteBehavior.Cascade, toCycle.DeleteBehavior);

        // The key of the cycles that it points at is the one that the calls of the roles and
        // the checks of the Risk Guard point at, under the name that it had before.
        Assert.Equal("ak_decision_cycles_id_user_id", toCycle.PrincipalKey.GetName());
        Assert.All(
            context.Model.FindEntityType(typeof(RoleOutput))!.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(DecisionCycle)),
            key => Assert.Same(toCycle.PrincipalKey, key.PrincipalKey));
    }

    [Theory]
    [InlineData(typeof(AnalystCall))]
    [InlineData(typeof(ShadowTrade))]
    public void A_row_says_the_mode_and_names_no_agent(Type entity)
    {
        using var context = ModelOnly.Context();
        var columns = context.Model.FindEntityType(entity)!.GetProperties().Select(property => property.GetColumnName()).ToList();

        // What an agent did is stored with the mode it did it in (FR-BIN-11), together with
        // the market, the symbol, and the timeframe that its agent had then.
        Assert.Contains("mode", columns);
        Assert.Contains("market", columns);
        Assert.Contains("symbol", columns);
        Assert.Contains("timeframe", columns);

        // It hangs on its cycle, and so on the agent of that cycle: it names the agent in no
        // column of its own, which a deleted agent would have to be taken out of.
        Assert.DoesNotContain("agent_id", columns);
    }

    [Fact]
    public void Experience_cannot_reach_the_orders_of_the_Exchange_Gateway_so_a_shadow_trade_places_none()
    {
        // The orders of the gateway are in a namespace of their own, which the architecture
        // rules open to the gateway itself and to the Decision Engine, and to nothing else.
        // DependencyRuleTests holds every type of the application to that.
        var mayPlaceOrders = ComponentRules.Rules.RestrictedNamespaces["ExchangeGateway.Trading"];

        Assert.DoesNotContain(ComponentRules.Experience, mayPlaceOrders);
        Assert.Equal([ComponentRules.DecisionEngine, ComponentRules.ExchangeGateway], mayPlaceOrders.Order(StringComparer.Ordinal));

        // And what a shadow trade is, and what evaluates it, is of Experience.
        Assert.All(
            new[] { typeof(ShadowTrade), typeof(ShadowTradeRule), typeof(AnalystCall), typeof(CallScore) },
            type => Assert.Equal($"Ecnanib.{ComponentRules.Experience}", type.Namespace));
    }
}
