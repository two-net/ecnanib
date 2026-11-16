using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Agents;

/// <summary>
/// A trading agent of a user, as a row of the <c>agents</c> table: one Binance market, one
/// symbol, one timeframe, with the capital and the risk settings its user gave it (FR-AGT-2).
/// See docs/agents.md.
/// </summary>
/// <remarks>
/// The row names no Binance credential. A user has one credential for each environment
/// (FR-KEY-2), so the credential of an agent is the one of its user for its mode, and is found
/// that way when it is needed. A key that pointed at the credential's row would delete the agent
/// with the key, or keep the key from being deleted.
/// </remarks>
/// <param name="id">Assigned by the application when the agent is created.</param>
/// <param name="userId">The user whose agent it is.</param>
/// <param name="name">The name its user gave it. A user has no two agents of one name (FR-AGT-1).</param>
/// <param name="mode">Binance's test environment or the real exchange. A new agent is in Demo (FR-AGT-7).</param>
/// <param name="market">The market it trades on (FR-AGT-4).</param>
/// <param name="symbol">The symbol it trades: one of the configuration file that its market has (FR-AGT-6).</param>
/// <param name="quoteAsset">The asset that the symbol is priced in, as Binance names it, which the allocated capital is in.</param>
/// <param name="timeframe">The candle length it works on (FR-AGT-3).</param>
/// <param name="allocatedCapital">The capital its user allocated to it, in the quote asset.</param>
/// <param name="riskPerTradePercent">The share of its equity that it risks on one trade, in percent.</param>
/// <param name="minRewardToRisk">The lowest reward-to-risk ratio that a plan of it may have at entry.</param>
/// <param name="leverageCap">The highest leverage it uses on Futures.</param>
/// <param name="liquidationBuffer">How many times further from its entry than the stop-loss the liquidation price must be on Futures.</param>
/// <param name="dailyLossPercent">The loss in one day that halts it, as a share of its equity in percent.</param>
/// <param name="drawdownPercent">The fall from its peak equity that halts it, in percent.</param>
/// <param name="maxEntryDistancePercent">How far an entry price may be from the current price, in percent.</param>
/// <param name="state">Its state (SRS 3.3).</param>
/// <param name="stateCause">Why it is in that state.</param>
/// <param name="stateChangedAt">Since when it is.</param>
/// <param name="createdAt">When it was created.</param>
public sealed class Agent(
    Guid id,
    Guid userId,
    string name,
    TradingMode mode,
    Market market,
    string symbol,
    string quoteAsset,
    Timeframe timeframe,
    decimal allocatedCapital,
    decimal riskPerTradePercent,
    decimal minRewardToRisk,
    int leverageCap,
    decimal liquidationBuffer,
    decimal dailyLossPercent,
    decimal drawdownPercent,
    decimal maxEntryDistancePercent,
    AgentState state,
    AgentStateCause stateCause,
    DateTimeOffset stateChangedAt,
    DateTimeOffset createdAt) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public string Name { get; } = name;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public string QuoteAsset { get; } = quoteAsset;

    public Timeframe Timeframe { get; } = timeframe;

    public decimal AllocatedCapital { get; } = allocatedCapital;

    public decimal RiskPerTradePercent { get; } = riskPerTradePercent;

    public decimal MinRewardToRisk { get; } = minRewardToRisk;

    public int LeverageCap { get; } = leverageCap;

    public decimal LiquidationBuffer { get; } = liquidationBuffer;

    public decimal DailyLossPercent { get; } = dailyLossPercent;

    public decimal DrawdownPercent { get; } = drawdownPercent;

    public decimal MaxEntryDistancePercent { get; } = maxEntryDistancePercent;

    public AgentState State { get; } = state;

    public AgentStateCause StateCause { get; } = stateCause;

    public DateTimeOffset StateChangedAt { get; } = stateChangedAt;

    public DateTimeOffset CreatedAt { get; } = createdAt;
}

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(agent => agent.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(agent => agent.Id).ValueGeneratedNever();
        builder.Property(agent => agent.UserId);
        builder.Property(agent => agent.Name);
        builder.Property(agent => agent.Mode);
        builder.Property(agent => agent.Market);
        builder.Property(agent => agent.Symbol);
        builder.Property(agent => agent.QuoteAsset);
        builder.Property(agent => agent.Timeframe);
        builder.Property(agent => agent.AllocatedCapital);
        builder.Property(agent => agent.RiskPerTradePercent);
        builder.Property(agent => agent.MinRewardToRisk);
        builder.Property(agent => agent.LeverageCap);
        builder.Property(agent => agent.LiquidationBuffer);
        builder.Property(agent => agent.DailyLossPercent);
        builder.Property(agent => agent.DrawdownPercent);
        builder.Property(agent => agent.MaxEntryDistancePercent);
        builder.Property(agent => agent.State);
        builder.Property(agent => agent.StateCause);
        builder.Property(agent => agent.StateChangedAt);
        builder.Property(agent => agent.CreatedAt);

        // A user has no two agents of one name (FR-AGT-1). The index also finds the agents of a user.
        builder.HasIndex(agent => new { agent.UserId, agent.Name }).IsUnique();

        // At most one active agent for each Binance credential, market, and symbol (FR-AGT-9).
        // A user has one credential for each environment, so the credential is the user and the
        // mode. An agent is active in every state but Draft and Stopped: the database holds to
        // that whatever changes a state, and for a state that is added later.
        builder.HasIndex(agent => new { agent.UserId, agent.Mode, agent.Market, agent.Symbol })
            .IsUnique()
            .HasFilter("state NOT IN ('Draft', 'Stopped')");

        // The agents belong to the account and go with it when it is deleted (FR-ACC-9).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(agent => agent.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
