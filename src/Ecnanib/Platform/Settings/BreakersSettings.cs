namespace Ecnanib.Platform.Settings;

/// <summary>
/// The <c>Breakers</c> section: when the calls to an AI provider, and the calls with one key for
/// it, are paused after failures in a row, and for how long (NFR-REL-4). See docs/reliability.md.
/// </summary>
/// <param name="AiProvider">The breaker of each AI provider, which counts the calls of all users.</param>
/// <param name="AiKey">The breaker of each key for an AI provider, which counts the calls with that key.</param>
public sealed record BreakersSettings(AiProviderBreakerSettings AiProvider, AiKeyBreakerSettings AiKey)
{
    // What a breaker may be set to. A hundred failures in a row are no breaker any more, and a
    // pause of more than a day is one that nobody waits for.
    internal const int MostFailures = 100;
    internal const int MostPauseSeconds = 86400;

    internal static BreakersSettings Read(SettingsReader file) =>
        new(AiProviderBreakerSettings.Read(file, "Breakers:AiProvider"), AiKeyBreakerSettings.Read(file, "Breakers:AiKey"));

    /// <summary>
    /// The longest pause of a breaker, which is no shorter than its first: a pause grows from
    /// the first to the longest, and a file that says it the other way round says no pause that
    /// anyone could tell.
    /// </summary>
    internal static int LongestPause(SettingsReader file, string key, int pauseSeconds)
    {
        var longestKey = $"{key}:LongestPauseSeconds";
        var longest = file.WholeNumber(longestKey, minimum: 1, maximum: MostPauseSeconds);

        if (longest > 0 && pauseSeconds > 0 && longest < pauseSeconds)
        {
            file.Problem(longestKey, "must not be less than PauseSeconds");
        }

        return longest;
    }
}

/// <summary>The breaker of an AI provider.</summary>
/// <param name="Failures">
/// How many calls to the provider in a row, of all users, may end with no answer or with a fault
/// of the provider before its calls are paused: from 1 to 100.
/// </param>
/// <param name="PauseSeconds">How long the first pause lasts: from a second to a day.</param>
/// <param name="LongestPauseSeconds">
/// How long a pause lasts at the most. A pause that follows another, with no answer between
/// them, lasts twice as long as the one before it, up to this: from <paramref name="PauseSeconds"/>
/// to a day.
/// </param>
public sealed record AiProviderBreakerSettings(int Failures, int PauseSeconds, int LongestPauseSeconds)
{
    internal static AiProviderBreakerSettings Read(SettingsReader file, string key)
    {
        var failures = file.WholeNumber($"{key}:Failures", minimum: 1, maximum: BreakersSettings.MostFailures);
        var pauseSeconds = file.WholeNumber($"{key}:PauseSeconds", minimum: 1, maximum: BreakersSettings.MostPauseSeconds);

        return new AiProviderBreakerSettings(failures, pauseSeconds, BreakersSettings.LongestPause(file, key, pauseSeconds));
    }
}

/// <summary>The breaker of a key for an AI provider.</summary>
/// <param name="Failures">
/// How many calls with the key in a row the provider may refuse before the calls with it are
/// paused: from 1 to 100.
/// </param>
/// <param name="PauseSeconds">How long the first pause lasts: from a second to a day.</param>
/// <param name="LongestPauseSeconds">
/// How long a pause lasts at the most, as <see cref="AiProviderBreakerSettings.LongestPauseSeconds"/>
/// says it of a provider.
/// </param>
/// <param name="TripsBeforeAttention">
/// How many pauses in a row, with no answer between them, move the agent whose call began the
/// last of them to Attention required: from 1 to 100.
/// </param>
/// <param name="ProviderWideKeys">
/// How many keys of one provider that are refused at the same time say that the trouble is not
/// of a key: no agent is moved for its key then. From 2 to 10000: a key counts itself.
/// </param>
public sealed record AiKeyBreakerSettings(
    int Failures, int PauseSeconds, int LongestPauseSeconds, int TripsBeforeAttention, int ProviderWideKeys)
{
    private const int MostTrips = 100;
    private const int MostKeys = 10000;

    internal static AiKeyBreakerSettings Read(SettingsReader file, string key)
    {
        var failures = file.WholeNumber($"{key}:Failures", minimum: 1, maximum: BreakersSettings.MostFailures);
        var pauseSeconds = file.WholeNumber($"{key}:PauseSeconds", minimum: 1, maximum: BreakersSettings.MostPauseSeconds);

        return new AiKeyBreakerSettings(
            failures,
            pauseSeconds,
            BreakersSettings.LongestPause(file, key, pauseSeconds),
            file.WholeNumber($"{key}:TripsBeforeAttention", minimum: 1, maximum: MostTrips),
            file.WholeNumber($"{key}:ProviderWideKeys", minimum: 2, maximum: MostKeys));
    }
}
