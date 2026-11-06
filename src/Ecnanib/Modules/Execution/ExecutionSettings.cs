using System.Globalization;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Order execution's settings in the Agents section. Read() is the only way in. A missing value takes its default, and
/// an invalid one stops startup with a message that names it (CFG-03). See Order execution in docs/architecture.md.
/// </summary>
internal sealed class ExecutionSettings
{
    public const string PendingEntryCheckKey = "Agents:PendingEntryCheckSeconds";
    public const string ProtectiveStopKey = "Agents:ProtectiveStopSeconds";
    public const string MaxLimitTtlKey = "Agents:MaxLimitTtlCandles";

    public const int DefaultPendingEntryCheckSeconds = 5;
    public const int DefaultProtectiveStopSeconds = 9;
    public const int DefaultMaxLimitTtlCandles = 10;

    /// <summary>A fill must be seen with time left to place its stop within AC-02's 10 seconds.</summary>
    public const int MaximumPendingEntryCheckSeconds = 5;

    public const int MinimumProtectiveStopSeconds = 5;

    /// <summary>AC-02: a stop rests within 10 seconds of a fill, or the position is closed.</summary>
    public const int MaximumProtectiveStopSeconds = 10;

    /// <summary>How long a fill may wait to be seen, at most, and still leave this long to protect it.</summary>
    public const int MinimumProtectingSeconds = 3;

    public const int MaximumMaxLimitTtlCandles = 100;

    public ExecutionSettings(TimeSpan pendingEntryCheck, TimeSpan protectiveStop, int maxLimitTtlCandles)
    {
        PendingEntryCheck = pendingEntryCheck;
        ProtectiveStop = protectiveStop;
        MaxLimitTtlCandles = maxLimitTtlCandles;
    }

    public static ExecutionSettings Default { get; } = new(
        TimeSpan.FromSeconds(DefaultPendingEntryCheckSeconds), TimeSpan.FromSeconds(DefaultProtectiveStopSeconds), DefaultMaxLimitTtlCandles);

    /// <summary>How often an unfilled entry, and anything else not yet settled, is checked with Binance.</summary>
    public TimeSpan PendingEntryCheck { get; }

    /// <summary>
    /// How long after a fill its stop may take to rest on Binance. After that, what filled is closed at market
    /// (FR-EXE-02, AC-02).
    /// </summary>
    public TimeSpan ProtectiveStop { get; }

    /// <summary>The longest time-to-live a limit entry may have, in candles (FR-EXE-01).</summary>
    public int MaxLimitTtlCandles { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static ExecutionSettings Read(IConfiguration configuration)
    {
        var check = ReadInteger(configuration, PendingEntryCheckKey, DefaultPendingEntryCheckSeconds, 1, MaximumPendingEntryCheckSeconds);
        var stop = ReadInteger(
            configuration, ProtectiveStopKey, DefaultProtectiveStopSeconds, MinimumProtectiveStopSeconds, MaximumProtectiveStopSeconds);
        var ttl = ReadInteger(configuration, MaxLimitTtlKey, DefaultMaxLimitTtlCandles, 1, MaximumMaxLimitTtlCandles);

        if (stop < check + MinimumProtectingSeconds)
        {
            throw new InvalidOperationException(
                $"{ProtectiveStopKey} must be at least {MinimumProtectingSeconds} more than {PendingEntryCheckKey}, "
                + "so a fill seen late still has time to be protected.");
        }

        return new ExecutionSettings(TimeSpan.FromSeconds(check), TimeSpan.FromSeconds(stop), ttl);
    }

    private static int ReadInteger(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{key} must be a whole number.");
        }

        if (number < minimum || number > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }

        return number;
    }
}
