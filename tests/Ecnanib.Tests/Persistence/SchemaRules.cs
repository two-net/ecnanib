using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Persistence;

/// <summary>The application's model without a database, as the tool that generates migrations sees it.</summary>
internal static class ModelOnly
{
    public static EcnanibDbContext Context() =>
        new(new DbContextOptionsBuilder<EcnanibDbContext>().UseNpgsql().Options);
}

/// <summary>
/// Finds the columns of a model that cannot hold what the application stores: prices,
/// quantities, and money as exact decimals, and timestamps in UTC (NFR-DAT-8).
/// </summary>
/// <remarks>
/// It forbids every floating-point column, not only those that hold money, because it cannot
/// tell what a column holds. A number that is neither a price, a quantity, nor money loses
/// nothing as a decimal.
/// </remarks>
internal static class SchemaRules
{
    private static readonly string[] InexactColumnTypes = ["real", "double precision", "float4", "float8", "money"];

    public static IReadOnlyList<string> FindViolations(IModel model) =>
    [
        .. from entity in model.GetEntityTypes()
           from property in entity.GetProperties()
           let column = property.GetColumnType()
           let reason = ReasonAgainst(property.ClrType, column)
           where reason is not null
           orderby entity.Name, property.Name
           select $"{entity.ClrType.Name}.{property.Name} ({column}): {reason}",
    ];

    private static string? ReasonAgainst(Type type, string column)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        value = value.IsArray ? value.GetElementType()! : value;
        value = Nullable.GetUnderlyingType(value) ?? value;

        if (value == typeof(float) || value == typeof(double) || value == typeof(Half))
        {
            return "a floating-point number is not exact. Use decimal.";
        }

        if (InexactColumnTypes.Any(inexact => column.StartsWith(inexact, StringComparison.OrdinalIgnoreCase)))
        {
            return "the column type is not an exact decimal. Use numeric, the default for decimal.";
        }

        // A timestamp that does not say "with time zone", with a precision or without one.
        if (column.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
            && !column.Equals("timestamptz", StringComparison.OrdinalIgnoreCase)
            && !column.EndsWith("with time zone", StringComparison.OrdinalIgnoreCase))
        {
            return "the column type does not store a moment in UTC. Use timestamp with time zone, the default.";
        }

        return null;
    }
}
