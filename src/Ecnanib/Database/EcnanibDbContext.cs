using Ecnanib.Database.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Database;

/// <summary>
/// The one PostgreSQL database, reached only through this context (DB-01, NFR-DATA-01). Resolve it through
/// <see cref="IDbContextFactory{TContext}"/> from long-lived components and services; the scoped registration is
/// for per-request work. Conventions: snake_case identifiers, enums as <c>text</c>, <see cref="DateTimeOffset"/>
/// as UTC <c>timestamp with time zone</c>, <c>decimal</c> as unbounded <c>numeric</c>, open-ended structures as
/// <c>jsonb</c>. No table is partitioned (DB-03).
/// </summary>
public sealed class EcnanibDbContext : DbContext, IDataProtectionKeyContext
{
    public EcnanibDbContext(DbContextOptions<EcnanibDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<ExchangeKey> ExchangeKeys => Set<ExchangeKey>();

    public DbSet<ProviderKey> ProviderKeys => Set<ProviderKey>();

    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();

    public DbSet<ProviderSyncState> ProviderSyncStates => Set<ProviderSyncState>();

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<CouncilAssignment> CouncilAssignments => Set<CouncilAssignment>();

    public DbSet<AgentChange> AgentChanges => Set<AgentChange>();

    public DbSet<EntryWindow> EntryWindows => Set<EntryWindow>();

    public DbSet<Candle> Candles => Set<Candle>();

    public DbSet<Evaluation> Evaluations => Set<Evaluation>();

    public DbSet<Decision> Decisions => Set<Decision>();

    public DbSet<DecisionRoleOutput> DecisionRoleOutputs => Set<DecisionRoleOutput>();

    public DbSet<DeliberationTranscript> DeliberationTranscripts => Set<DeliberationTranscript>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Trade> Trades => Set<Trade>();

    public DbSet<ShadowOutcome> ShadowOutcomes => Set<ShadowOutcome>();

    public DbSet<ExperienceStat> ExperienceStats => Set<ExperienceStat>();

    public DbSet<LogEntry> LogEntries => Set<LogEntry>();

    public DbSet<SystemKey> SystemKeys => Set<SystemKey>();

    public DbSet<DisclaimerAcceptance> DisclaimerAcceptances => Set<DisclaimerAcceptance>();

    /// <summary>ASP.NET Data Protection key ring, so the system's own signing keys live in the database (FR-CFG-05, A-01).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcnanibDbContext).Assembly);
    }
}
