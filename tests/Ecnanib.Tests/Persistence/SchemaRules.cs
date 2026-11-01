using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Checks a model against the data rules in SRS 6.2. It reads every mapped column, so it covers
/// entities that don't exist yet. It takes the context as a parameter so that SchemaRulesGuardTests
/// can prove it catches real violations.
/// </summary>
internal static class SchemaRules
{
    private static readonly string[] BinaryFloatingPointColumns = ["real", "float4", "double precision", "float8"];

    public static IEnumerable<string> FindViolations(DbContext context) =>
        from entity in context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
        from property in entity.GetFlattenedProperties()
        let rule = BrokenRule(property)
        where rule is not null
        select $"{property.DeclaringType.DisplayName()}.{property.Name}: {rule}";

    private static string? BrokenRule(IProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var column = property.GetColumnType();

        if (type == typeof(float) || type == typeof(double) || BinaryFloatingPointColumns.Contains(column))
        {
            return "binary floating point (DATA-R-02)";
        }

        if ((type == typeof(DateTime) || type == typeof(DateTimeOffset)) && column != "timestamp with time zone")
        {
            return "timestamp not stored as UTC (DATA-R-01)";
        }

        return null;
    }
}
