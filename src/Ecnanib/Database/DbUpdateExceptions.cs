using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Database;

/// <summary>The PostgreSQL errors the modules handle themselves: a unique-key race is settled by reading the row that won.</summary>
public static class DbUpdateExceptions
{
    /// <summary>True when the save failed on a primary key or unique index (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>True when a save or a bulk statement failed on a foreign key (SQLSTATE 23503), whether EF wrapped the provider's exception or not.</summary>
    public static bool IsForeignKeyViolation(Exception exception) =>
        (exception is DbUpdateException ? exception.InnerException : exception) is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
}
