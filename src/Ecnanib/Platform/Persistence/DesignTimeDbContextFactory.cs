using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecnanib.Platform.Persistence;

/// <summary>Lets `dotnet ef` generate migrations without configuration or a running database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EcnanibDbContext>
{
    public EcnanibDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EcnanibDbContext>();
        options.UsePostgres(connectionString: null);
        return new EcnanibDbContext(options.Options);
    }
}
