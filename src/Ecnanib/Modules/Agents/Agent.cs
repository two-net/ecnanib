using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// A user's trading agent (SRS 6.1): its settings (FR-AGT-02) and its status (FR-AGT-04). The risk settings,
/// budget and experience memory are columns of their own, so each is a plain value in the schema. There's
/// no foreign key to <c>users</c> or <c>credentials</c>, because other modules own those tables: account
/// deletion reaches this one through the port Identity declares (#51). See Agents in docs/architecture.md.
/// </summary>
internal sealed class Agent
{
    /// <summary>The longest enum name stored here, with room to spare.</summary>
    public const int EnumMaxLength = 32;

    /// <summary>An AI provider's name, as Catalog stores it.</summary>
    public const int ProviderMaxLength = 32;

    /// <summary>A model's ID, as Catalog stores it.</summary>
    public const int ModelMaxLength = 256;

    /// <summary>An asset's name, as Market Data stores it.</summary>
    public const int AssetMaxLength = 32;

    /// <summary>The unique index on a user's names, which an archived agent leaves.</summary>
    public const string NameIndex = "ix_agents_user_id_name_key";

    /// <summary>FR-AGT-10's unique index. The statuses are the ones that may hold exchange state.</summary>
    public const string SlotIndex = "ix_agents_trading_slot";

    /// <summary>The statuses that hold an exchange key, market and pair (FR-AGT-10, AC-13).</summary>
    public static readonly AgentStatus[] SlotStatuses = [AgentStatus.Active, AgentStatus.Paused, AgentStatus.Error];

    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required string Name { get; set; }

    /// <summary><c>lower(name)</c>, computed by the database, which the unique index on names uses.</summary>
    public string NameKey { get; private set; } = "";

    public string? Description { get; set; }

    public required Market Market { get; set; }

    public required TradingEnvironment Environment { get; set; }

    public required string Symbol { get; set; }

    public required string QuoteAsset { get; set; }

    public required Timeframe Timeframe { get; set; }

    public Guid? ExchangeCredentialId { get; set; }

    public required ExecutionMode ExecutionMode { get; set; }

    public bool AutoExecuteRiskReducing { get; set; }

    public decimal? CapitalAllocation { get; set; }

    public decimal RiskMaxRiskPerTradePercent { get; set; }

    public decimal? RiskMaxPositionNotional { get; set; }

    public int RiskMaxLeverage { get; set; }

    public decimal RiskDailyLossLimitPercent { get; set; }

    public decimal RiskWeeklyLossLimitPercent { get; set; }

    public int RiskConsecutiveLossesBeforePause { get; set; }

    public decimal RiskMinRewardToRisk { get; set; }

    public int RiskMaxNewEntriesPerDay { get; set; }

    public decimal RiskMinStopDistanceAtr { get; set; }

    public decimal RiskMaxStopDistanceAtr { get; set; }

    public decimal RiskLiquidationBufferMultiple { get; set; }

    public decimal RiskFundingFilterPercent { get; set; }

    public int RiskCooldownCyclesAfterLoss { get; set; }

    /// <summary>In <see cref="AiBudget.Currency"/>.</summary>
    public decimal? BudgetDailyUsd { get; set; }

    /// <summary>In <see cref="AiBudget.Currency"/>.</summary>
    public decimal? BudgetMonthlyUsd { get; set; }

    public long? BudgetDailyTokens { get; set; }

    public long? BudgetMonthlyTokens { get; set; }

    public bool ExperienceEnabled { get; set; }

    public int ReflectionsInPrompt { get; set; }

    public string? CustomInstructions { get; set; }

    public required AgentStatus Status { get; set; }

    public required AgentStatusReason StatusReason { get; set; }

    public required DateTimeOffset StatusChangedAt { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A concurrency token that every change increments, so an edit can't overwrite one it hasn't seen.</summary>
    public int Version { get; set; }

    public List<AgentRoleAssignment> Roles { get; init; } = [];

    public RiskSettings Risk
    {
        get => new()
        {
            MaxRiskPerTradePercent = RiskMaxRiskPerTradePercent,
            MaxPositionNotional = RiskMaxPositionNotional,
            MaxLeverage = RiskMaxLeverage,
            DailyLossLimitPercent = RiskDailyLossLimitPercent,
            WeeklyLossLimitPercent = RiskWeeklyLossLimitPercent,
            ConsecutiveLossesBeforePause = RiskConsecutiveLossesBeforePause,
            MinRewardToRisk = RiskMinRewardToRisk,
            MaxNewEntriesPerDay = RiskMaxNewEntriesPerDay,
            MinStopDistanceAtr = RiskMinStopDistanceAtr,
            MaxStopDistanceAtr = RiskMaxStopDistanceAtr,
            LiquidationBufferMultiple = RiskLiquidationBufferMultiple,
            FundingFilterPercent = RiskFundingFilterPercent,
            CooldownCyclesAfterLoss = RiskCooldownCyclesAfterLoss,
        };

        set
        {
            RiskMaxRiskPerTradePercent = value.MaxRiskPerTradePercent;
            RiskMaxPositionNotional = value.MaxPositionNotional;
            RiskMaxLeverage = value.MaxLeverage;
            RiskDailyLossLimitPercent = value.DailyLossLimitPercent;
            RiskWeeklyLossLimitPercent = value.WeeklyLossLimitPercent;
            RiskConsecutiveLossesBeforePause = value.ConsecutiveLossesBeforePause;
            RiskMinRewardToRisk = value.MinRewardToRisk;
            RiskMaxNewEntriesPerDay = value.MaxNewEntriesPerDay;
            RiskMinStopDistanceAtr = value.MinStopDistanceAtr;
            RiskMaxStopDistanceAtr = value.MaxStopDistanceAtr;
            RiskLiquidationBufferMultiple = value.LiquidationBufferMultiple;
            RiskFundingFilterPercent = value.FundingFilterPercent;
            RiskCooldownCyclesAfterLoss = value.CooldownCyclesAfterLoss;
        }
    }

    public AgentSettings Settings => new()
    {
        Name = Name,
        Description = Description,
        Market = Market,
        Environment = Environment,
        Symbol = Symbol,
        Timeframe = Timeframe,
        ExchangeCredentialId = ExchangeCredentialId,
        ExecutionMode = ExecutionMode,
        AutoExecuteRiskReducing = AutoExecuteRiskReducing,
        Roles = [.. Roles.OrderBy(role => role.Role).Select(role => role.ToAssignment())],
        CapitalAllocation = CapitalAllocation,
        Risk = Risk,
        Budget = new AiBudget(BudgetDailyUsd, BudgetMonthlyUsd, BudgetDailyTokens, BudgetMonthlyTokens),
        Experience = new ExperienceMemory(ExperienceEnabled, ReflectionsInPrompt),
        CustomInstructions = CustomInstructions,
    };

    /// <summary>
    /// Sets everything the user chooses but the roles, which are rows of their own: the caller replaces them.
    /// <paramref name="settings"/> has been cleaned and checked.
    /// </summary>
    public void Apply(AgentSettings settings, string quoteAsset)
    {
        Name = settings.Name;
        Description = settings.Description;
        Market = settings.Market;
        Environment = settings.Environment;
        Symbol = settings.Symbol;
        QuoteAsset = quoteAsset;
        Timeframe = settings.Timeframe;
        ExchangeCredentialId = settings.ExchangeCredentialId;
        ExecutionMode = settings.ExecutionMode;
        AutoExecuteRiskReducing = settings.AutoExecuteRiskReducing;
        CapitalAllocation = settings.CapitalAllocation;
        Risk = settings.Risk;
        BudgetDailyUsd = settings.Budget.DailyUsd;
        BudgetMonthlyUsd = settings.Budget.MonthlyUsd;
        BudgetDailyTokens = settings.Budget.DailyTokens;
        BudgetMonthlyTokens = settings.Budget.MonthlyTokens;
        ExperienceEnabled = settings.Experience.Enabled;
        ReflectionsInPrompt = settings.Experience.ReflectionsInPrompt;
        CustomInstructions = settings.CustomInstructions;
    }

    public AgentRecord ToRecord() =>
        new(Id, Settings, QuoteAsset, Status, StatusReason, StatusChangedAt, CreatedAt, UpdatedAt, Version);
}

/// <summary>The model, and optional fallback, that fills one role for one agent (SRS 6.1, FR-AGT-02).</summary>
internal sealed class AgentRoleAssignment
{
    public required Guid AgentId { get; init; }

    public required AiRole Role { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public string? FallbackProvider { get; init; }

    public string? FallbackModelId { get; init; }

    public static AgentRoleAssignment Of(Guid agentId, RoleAssignment assignment) => new()
    {
        AgentId = agentId,
        Role = assignment.Role,
        Provider = assignment.Model.Provider,
        ModelId = assignment.Model.ModelId,
        FallbackProvider = assignment.Fallback?.Provider,
        FallbackModelId = assignment.Fallback?.ModelId,
    };

    public RoleAssignment ToAssignment() => new(
        Role,
        new ModelRef(Provider, ModelId),
        FallbackProvider is { } provider && FallbackModelId is { } model ? new ModelRef(provider, model) : null);
}

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(agent => agent.Id);
        builder.Ignore(agent => agent.Risk);
        builder.Ignore(agent => agent.Settings);

        builder.Property(agent => agent.Name).HasMaxLength(AgentLimits.NameMaxLength);
        builder.Property(agent => agent.NameKey).HasMaxLength(AgentLimits.NameMaxLength).HasComputedColumnSql("lower(name)", stored: true);
        builder.Property(agent => agent.Description).HasMaxLength(AgentLimits.DescriptionMaxLength);
        builder.Property(agent => agent.Market).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.Environment).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.Symbol).HasMaxLength(AgentLimits.SymbolMaxLength);
        builder.Property(agent => agent.QuoteAsset).HasMaxLength(Agent.AssetMaxLength);
        builder.Property(agent => agent.Timeframe).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.ExecutionMode).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.CustomInstructions).HasMaxLength(AgentLimits.CustomInstructionsMaxLength);
        builder.Property(agent => agent.Status).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.StatusReason).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(agent => agent.Version).IsConcurrencyToken();

        builder.HasMany(agent => agent.Roles)
            .WithOne()
            .HasForeignKey(role => role.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(agent => agent.UserId);
        builder.HasIndex(agent => agent.ExchangeCredentialId);

        // Names are unique per user ignoring case, among the agents that aren't archived (FR-AGT-02).
        builder.HasIndex(agent => new { agent.UserId, agent.NameKey })
            .HasDatabaseName(Agent.NameIndex)
            .IsUnique()
            .HasFilter("status <> 'Archived'");

        // One agent per exchange key, market and pair may hold exchange state (FR-AGT-10, AC-13). Writes take
        // the user's lock and check first, so this is the backstop: a violation would be logged as an Error.
        builder.HasIndex(agent => new { agent.ExchangeCredentialId, agent.Market, agent.Symbol })
            .HasDatabaseName(Agent.SlotIndex)
            .IsUnique()
            .HasFilter("status IN ('Active', 'Paused', 'Error')");
    }
}

internal sealed class AgentRoleAssignmentConfiguration : IEntityTypeConfiguration<AgentRoleAssignment>
{
    public void Configure(EntityTypeBuilder<AgentRoleAssignment> builder)
    {
        builder.ToTable("agent_role_assignments");
        builder.HasKey(role => new { role.AgentId, role.Role });
        builder.Property(role => role.Role).HasConversion<string>().HasMaxLength(Agent.EnumMaxLength);
        builder.Property(role => role.Provider).HasMaxLength(Agent.ProviderMaxLength);
        builder.Property(role => role.ModelId).HasMaxLength(Agent.ModelMaxLength);
        builder.Property(role => role.FallbackProvider).HasMaxLength(Agent.ProviderMaxLength);
        builder.Property(role => role.FallbackModelId).HasMaxLength(Agent.ModelMaxLength);
    }
}
