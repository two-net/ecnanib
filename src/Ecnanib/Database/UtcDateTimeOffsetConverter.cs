using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecnanib.Database;

/// <summary>
/// Normalizes every <see cref="DateTimeOffset"/> to UTC on write. Npgsql rejects non-zero offsets for
/// <c>timestamp with time zone</c>, and all times in the system are UTC (SRS §1.4).
/// </summary>
internal sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetConverter()
        : base(value => value.ToUniversalTime(), value => value)
    {
    }
}
