using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// The application's one database: PostgreSQL, with the schema defined by the code (NFR-DAT-1,
/// NFR-DAT-2). The model is every <see cref="IEntityTypeConfiguration{TEntity}"/> in the
/// application, each beside the entity it maps, in the component that owns it. See
/// docs/persistence.md.
/// </summary>
/// <remarks>
/// There is no set property per entity, because the platform must not name a component's types.
/// A component reads and writes its entities through <see cref="DbContext.Set{TEntity}()"/>.
/// </remarks>
public sealed class EcnanibDbContext(DbContextOptions<EcnanibDbContext> options) : DbContext(options)
{
    /// <summary>
    /// What holds for every use of the context: the running application, the tool that generates
    /// migrations, and tests. It is set here, and not where the context is registered, so that
    /// none of them can leave a part out.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder

            // Tables and columns are named the way PostgreSQL names things: prompt_templates.is_active.
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(warnings => warnings

                // A configuration that cannot be created would otherwise leave its entity out of
                // the model with no more than a warning.
                .Throw(CoreEventId.SkippedEntityTypeConfigurationWarning)

                // A failure also throws. Whoever handles the exception logs it, once, so the
                // events that would log it a second time are kept for troubleshooting only. One
                // of them is not even an error: the first start on an empty database asks for
                // a migration history that is not there yet.
                .Log(
                    (RelationalEventId.ConnectionError, LogLevel.Debug),
                    (RelationalEventId.CommandError, LogLevel.Debug),
                    (RelationalEventId.TransactionError, LogLevel.Debug),
                    (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                    (CoreEventId.QueryIterationFailed, LogLevel.Debug)));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // An enum is stored as its name, so a row reads the same in the database as in the code.
        configurationBuilder.Properties<Enum>().HaveConversion<string>();

        configurationBuilder.Properties<AiProviderCode>().HaveConversion<AiProviderCodeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EcnanibDbContext).Assembly);

    /// <summary>Stores a provider's code as its text.</summary>
    private sealed class AiProviderCodeConverter()
        : ValueConverter<AiProviderCode, string>(code => code.Value, text => new AiProviderCode(text));
}
