using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// The application's one database context. It declares no <c>DbSet</c>s, so Platform references no
/// module type: a module keeps its entities internal, maps them with an
/// <see cref="IEntityTypeConfiguration{TEntity}"/> and reaches them with <c>Set&lt;T&gt;()</c>.
/// Use it through <see cref="IDbContextFactory{TContext}"/>. See docs/architecture.md.
/// </summary>
public sealed class EcnanibDbContext(DbContextOptions<EcnanibDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcnanibDbContext).Assembly);
}
