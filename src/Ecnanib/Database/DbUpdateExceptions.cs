using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Database;

/// <summary>The PostgreSQL errors the modules handle themselves: a unique-key race is settled by reading the row that won.</summary>
public static class DbUpdateExceptions
{
    /// <summary>True when the save failed on a primary key or unique index (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
