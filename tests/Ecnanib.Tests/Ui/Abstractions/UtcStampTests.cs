using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Tests.Ui.Abstractions;

/// <summary>NFR-USE-03: a stamp labelled UTC is the UTC instant in the invariant culture, whatever offset the value carries.</summary>
public class UtcStampTests
{
    private static readonly DateTimeOffset Offset = new(2026, 1, 5, 2, 30, 45, TimeSpan.FromHours(2));

    [Fact]
    public void MinutesAndSecondsAreUtcAndInvariant()
    {
        Assert.Equal("2026-01-05 00:30 UTC", UtcStamp.Minute(Offset));
        Assert.Equal("2026-01-05 00:30:45 UTC", UtcStamp.Second(Offset));
        Assert.Equal("2026-01-05 00:30 UTC", UtcStamp.Minute((DateTimeOffset?)Offset));
        Assert.Equal("2026-01-05 00:30:45 UTC", UtcStamp.Second((DateTimeOffset?)Offset));
    }

    [Fact]
    public void AMissingValueReadsNever()
    {
        Assert.Equal("never", UtcStamp.Minute(null));
        Assert.Equal("never", UtcStamp.Second(null));
    }
}
