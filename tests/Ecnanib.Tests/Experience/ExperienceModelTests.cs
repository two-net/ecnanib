using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// What Experience stores hangs on the cycle that it is of, by a key that the Decision Engine
/// declares, because Experience may not use it. Who left the platform-wide board points at the
/// account, by a key that Agents declares for the same reason. And shadow trades never place
/// orders (FR-EXP-5):
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

    [Fact]
    public void The_keys_of_a_lesson_are_named_for_their_tables_and_the_key_of_the_positions_keeps_its_name()
    {
        using var context = ModelOnly.Context();
        var mapped = context.Model.FindEntityType(typeof(Lesson))!;

        // The entity has three configurations: its own in Experience, and one for each of its
        // keys to what the Decision Engine keeps, each named so that it is applied after the
        // configuration of the table that it points at. All are of the one table, and its keys
        // are named for the tables, and not for what an entity was called before it had one.
        Assert.Equal("lessons", mapped.GetTableName());
        Assert.Equal("pk_lessons", mapped.FindPrimaryKey()!.GetName());
        Assert.Equal(["position_id"], mapped.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()));

        var toPosition = Assert.Single(mapped.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(Position));

        Assert.Equal("fk_lessons_positions_position_id_user_id", toPosition.GetConstraintName());
        Assert.Equal(["position_id", "user_id"], toPosition.Properties.Select(property => property.GetColumnName()));

        // It carries the owner along, and the lesson goes with its position, so with the agent
        // of that position and with the account (NFR-DAT-9, FR-ACC-9).
        Assert.Equal(["id", "user_id"], toPosition.PrincipalKey.Properties.Select(property => property.GetColumnName()));
        Assert.Equal(DeleteBehavior.Cascade, toPosition.DeleteBehavior);

        // The key of the positions that it points at is the one that the results of the
        // positions point at, under the name that it had before.
        Assert.Equal("ak_positions_id_user_id", toPosition.PrincipalKey.GetName());
        Assert.All(
            context.Model.FindEntityType(typeof(PositionResult))!.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(Position)),
            key => Assert.Same(toPosition.PrincipalKey, key.PrincipalKey));

        // A lesson names the version of the lesson instructions that it was written from
        // (FR-PRM-6), which is a row that is never deleted, and the model that wrote it, which
        // is one of the catalog and is never deleted either (FR-CAT-5).
        var toTemplate = Assert.Single(mapped.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(PromptTemplate));

        Assert.Equal("fk_lessons_prompt_templates_template_id", toTemplate.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, toTemplate.DeleteBehavior);

        var toModel = Assert.Single(mapped.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(Ecnanib.Catalog.AiModel));

        Assert.Equal("fk_lessons_ai_models_model_id", toModel.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, toModel.DeleteBehavior);
        Assert.Equal(3, mapped.GetForeignKeys().Count());
    }

    [Fact]
    public void Who_left_the_board_points_at_the_account_by_a_key_that_is_named_for_its_tables()
    {
        using var context = ModelOnly.Context();
        var mapped = context.Model.FindEntityType(typeof(BoardOptOut))!;

        // The entity has two configurations: its own in Experience, and that of its key to the
        // account in Agents, which may use both Experience and Accounts, and which is applied
        // first. Both are of the one table, and its keys are named for it.
        Assert.Equal("board_opt_outs", mapped.GetTableName());
        Assert.Equal("pk_board_opt_outs", mapped.FindPrimaryKey()!.GetName());

        // A user has left the board or has not: the row is found by its user.
        Assert.Equal(["user_id"], mapped.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()));
        Assert.Equal(["user_id", "opted_out_at"], mapped.GetProperties().Select(property => property.GetColumnName()));

        var toAccount = Assert.Single(mapped.GetForeignKeys());

        Assert.Equal(typeof(Ecnanib.Accounts.User), toAccount.PrincipalEntityType.ClrType);
        Assert.Equal("fk_board_opt_outs_users_user_id", toAccount.GetConstraintName());
        Assert.Equal(["user_id"], toAccount.Properties.Select(property => property.GetColumnName()));

        // It points at the account itself, and the row goes with it (NFR-DAT-9, FR-ACC-9).
        Assert.True(toAccount.PrincipalKey.IsPrimaryKey());
        Assert.Equal(DeleteBehavior.Cascade, toAccount.DeleteBehavior);

        // The key is the row's own: the database keeps no second index for it.
        Assert.Empty(mapped.GetIndexes());
    }

    [Fact]
    public void A_lesson_says_the_mode_and_names_its_agent_which_recall_and_reset_go_by()
    {
        using var context = ModelOnly.Context();
        var columns = context.Model.FindEntityType(typeof(Lesson))!.GetProperties().Select(property => property.GetColumnName()).ToList();

        // What an agent learned is stored with the mode it was learned in (FR-BIN-11), together
        // with the market, the symbol, and the timeframe of its position: what the lessons of
        // other agents are recalled by (FR-EXP-7).
        Assert.Contains("mode", columns);
        Assert.Contains("market", columns);
        Assert.Contains("symbol", columns);
        Assert.Contains("timeframe", columns);

        // And, unlike a call and a shadow trade, it names its agent in a column of its own:
        // the lessons of the agent are recalled first, and are what a reset deletes (FR-EXP-8),
        // and Experience cannot ask the positions whose they are. The row goes with its
        // position, which goes with the agent, so no deleted agent is left in the column.
        Assert.Contains("agent_id", columns);
        Assert.DoesNotContain(
            context.Model.FindEntityType(typeof(Lesson))!.GetForeignKeys(),
            key => key.Properties.Any(property => property.GetColumnName() == "agent_id"));
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
