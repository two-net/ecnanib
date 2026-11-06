using System.Globalization;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Order execution's settings in the Agents section, and reconciliation's interval in the Binance section, where SRS
/// 7.2 puts it. Read() is the only way in. A missing value takes its default, and
/// an invalid one stops startup with a message that names it (CFG-03). See Order execution in docs/architecture.md.
/// </summary>
internal sealed class ExecutionSettings
{
    public const string PendingEntryCheckKey = "Agents:PendingEntryCheckSeconds";
    public const string ProtectiveStopKey = "Agents:ProtectiveStopSeconds";
    public const string MaxLimitTtlKey = "Agents:MaxLimitTtlCandles";
    public const string ReconcileIntervalKey = "Binance:ReconcileIntervalSeconds";

    public const int DefaultPendingEntryCheckSeconds = 5;
    public const int DefaultProtectiveStopSeconds = 9;
    public const int DefaultMaxLimitTtlCandles = 10;
    public const int DefaultReconcileIntervalSeconds = 60;

    /// <summary>Below this, the reads of every agent with a position would crowd out everything else.</summary>
    public const int MinimumReconcileIntervalSeconds = 30;

    public const int MaximumReconcileIntervalSeconds = 3600;

    /// <summary>A fill must be seen with time left to place its stop within AC-02's 10 seconds.</summary>
    public const int MaximumPendingEntryCheckSeconds = 5;

    public const int MinimumProtectiveStopSeconds = 5;

    /// <summary>AC-02: a stop rests within 10 seconds of a fill, or the position is closed.</summary>
    public const int MaximumProtectiveStopSeconds = 10;

    /// <summary>How long a fill may wait to be seen, at most, and still leave this long to protect it.</summary>
    public const int MinimumProtectingSeconds = 3;

    public const int MaximumMaxLimitTtlCandles = 100;

    public ExecutionSettings(TimeSpan pendingEntryCheck, TimeSpan protectiveStop, int maxLimitTtlCandles, TimeSpan reconcileInterval)
    {
        PendingEntryCheck = pendingEntryCheck;
        ProtectiveStop = protectiveStop;
        MaxLimitTtlCandles = maxLimitTtlCandles;
        ReconcileInterval = reconcileInterval;
    }

    public static ExecutionSettings Default { get; } = new(
        TimeSpan.FromSeconds(DefaultPendingEntryCheckSeconds),
        TimeSpan.FromSeconds(DefaultProtectiveStopSeconds),
        DefaultMaxLimitTtlCandles,
        TimeSpan.FromSeconds(DefaultReconcileIntervalSeconds));

    /// <summary>How often an unfilled entry, and anything else not yet settled, is checked with Binance.</summary>
    public TimeSpan PendingEntryCheck { get; }

    /// <summary>
    /// How long after a fill its stop may take to rest on Binance. After that, what filled is closed at market
    /// (FR-EXE-02, AC-02).
    /// </summary>
    public TimeSpan ProtectiveStop { get; }

    /// <summary>The longest time-to-live a limit entry may have, in candles (FR-EXE-01).</summary>
    public int MaxLimitTtlCandles { get; }

    /// <summary>
    /// How often every agent with an open position or order is reconciled with Binance, whatever its status
    /// (FR-EXE-06, FR-AGT-06). Every cycle reconciles too.
    /// </summary>
    public TimeSpan ReconcileInterval { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static ExecutionSettings Read(IConfiguration configuration)
    {
        var check = ReadInteger(configuration, PendingEntryCheckKey, DefaultPendingEntryCheckSeconds, 1, MaximumPendingEntryCheckSeconds);
        var stop = ReadInteger(
            configuration, ProtectiveStopKey, DefaultProtectiveStopSeconds, MinimumProtectiveStopSeconds, MaximumProtectiveStopSeconds);
        var ttl = ReadInteger(configuration, MaxLimitTtlKey, DefaultMaxLimitTtlCandles, 1, MaximumMaxLimitTtlCandles);
        var reconcile = ReadInteger(
            configuration, ReconcileIntervalKey, DefaultReconcileIntervalSeconds, MinimumReconcileIntervalSeconds, MaximumReconcileIntervalSeconds);

        if (stop < check + MinimumProtectingSeconds)
        {
            throw new InvalidOperationException(
                $"{ProtectiveStopKey} must be at least {MinimumProtectingSeconds} more than {PendingEntryCheckKey}, "
                + "so a fill seen late still has time to be protected.");
        }

        return new ExecutionSettings(TimeSpan.FromSeconds(check), TimeSpan.FromSeconds(stop), ttl, TimeSpan.FromSeconds(reconcile));
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
