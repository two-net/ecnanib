using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// The application's one database context. It declares no <c>DbSet</c>s, so Platform references no
/// module type: a module keeps its entities internal, maps them with an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> and reaches them with <c>Set&lt;T&gt;()</c>.
/// Use it through <see cref="IDbContextFactory{TContext}"/>. See docs/architecture.md.
/// </summary>
public sealed class EcnanibDbContext : DbContext
{
    private List<Action>? _afterCommit;

    public EcnanibDbContext(DbContextOptions<EcnanibDbContext> options)
        : base(options) =>
        SavedChanges += (_, _) =>
        {
            // Inside the caller's transaction nothing is visible yet, so that commit runs them instead.
            if (Database.CurrentTransaction is null)
            {
                RunAfterCommit();
            }
        };

    /// <summary>
    /// Runs <paramref name="action"/> once what this context changes is visible to others: when the next
    /// <c>SaveChanges</c> outside a transaction ends, or when the transaction it's in commits. It's for telling
    /// observers, such as the Web UI's live updates, so it must return at once and never throw. A rollback keeps it
    /// for the context's next commit, and disposing the context drops it, so it runs at most once, and possibly
    /// after a change that didn't happen: an observer only ever re-reads, so that costs one read. A bulk
    /// <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> outside a transaction runs nothing: tell observers after it
    /// returns. See Live updates in docs/architecture.md.
    /// </summary>
    public void AfterCommit(Action action) => (_afterCommit ??= []).Add(action);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(CommitInterceptor.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcnanibDbContext).Assembly);

    private void RunAfterCommit()
    {
        if (_afterCommit is not { } actions)
        {
            return;
        }

        _afterCommit = null;

        foreach (var action in actions)
        {
            action();
        }
    }

    /// <summary>
    /// Hears every commit, including the one <c>SaveChanges</c> starts for itself. One instance, so EF Core's
    /// internal service provider is shared by every context, as it would be with no interceptor.
    /// </summary>
    private sealed class CommitInterceptor : DbTransactionInterceptor
    {
        public static CommitInterceptor Instance { get; } = new();

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            (eventData.Context as EcnanibDbContext)?.RunAfterCommit();

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            (eventData.Context as EcnanibDbContext)?.RunAfterCommit();
            return Task.CompletedTask;
        }
    }
}
