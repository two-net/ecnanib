using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>How a call to a model ended, as the breakers count it.</summary>
internal enum CallEnd
{
    /// <summary>The provider answered the call with the key: it works, and so does the key.</summary>
    Healthy,

    /// <summary>
    /// The provider answered, and what it said has a handling of its own: a key that it does not
    /// know, or a model that it does not have. The provider works, and nothing is said of the key.
    /// </summary>
    Answered,

    /// <summary>
    /// The provider answered, and refused the call: a limit on the requests of the key, an
    /// account that has run out, or a request that it would not take. The provider works.
    /// </summary>
    KeyRefused,

    /// <summary>
    /// No answer, or one that says the provider is at fault: no connection, an answer that broke
    /// off, a call that took too long, or a fault that the provider reports of itself.
    /// </summary>
    ProviderFault,
}

/// <summary>
/// The breakers of the calls to the models (NFR-REL-4): one for each provider, which counts the
/// calls of all users, and one for each key, which counts the calls with it. A breaker pauses
/// its calls after failures in a row, and lets them be made again later. See docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// After a pause every call passes again, and the first that fails begins the next pause at
/// once, which is twice as long, up to the longest of the configuration file. An answer ends
/// it all. No single call is sent ahead to try the provider: each call is made with the key of
/// some user for a cycle of theirs, and a provider that answers again answers them all.
/// </para>
/// <para>
/// It keeps what it knows in memory, for this process, as the limiter of the exchange does: a
/// start begins with every call passing. A key is known here by a mark that is worked out from
/// it with a secret of this process, which is made at the start and kept nowhere. The mark says
/// only that two calls came with the same key. It is in no log event, and nothing leads back
/// from it to the key.
/// </para>
/// <para>
/// A pause is told from the time of day of the application's clock, and no timer runs for it:
/// whether a call passes is asked when the call is made.
/// </para>
/// </remarks>
internal sealed class AiCallBreakers(
    BreakersSettings settings,
    AiProvidersSettings providers,
    TimeProvider clock,
    ILogger<AiCallBreakers> logger) : IAiCallPauses
{
    /// <summary>
    /// The most keys that are counted at once. Only a key that is refused is counted, so this is
    /// far more than there are: it keeps what a fault could make of the memory within bounds.
    /// </summary>
    public const int MostKeys = 4096;

    private readonly Lock _gate = new();
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<AiProviderCode, Breaker> _providers = [];
    private readonly Dictionary<(AiProviderCode Provider, KeyMark Key), Breaker> _keys = [];

    // How many calls have asked to pass. A breaker remembers the number at its latest pause, so
    // that a call which was under way then is told from one that was made after it.
    private long _entered;

    /// <summary>
    /// Asks whether a call may be made. One that may not was not made: its pass says how it
    /// ended, and is not handed back.
    /// </summary>
    /// <param name="provider">The provider of the call.</param>
    /// <param name="apiKey">The key of the call.</param>
    public CallPass Enter(AiProviderCode provider, string apiKey)
    {
        var key = MarkOf(apiKey);
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            if (_providers.TryGetValue(provider, out var ofProvider) && ofProvider.IsPaused(now))
            {
                return new CallPass(provider, key, Entered: 0, AiCallOutcome.ProviderPaused);
            }

            if (_keys.TryGetValue((provider, key), out var ofKey) && ofKey.IsPaused(now))
            {
                return new CallPass(provider, key, Entered: 0, AiCallOutcome.KeyPaused);
            }

            return new CallPass(provider, key, ++_entered, Paused: null);
        }
    }

    /// <summary>
    /// Takes how a call that passed ended, and gives its result back: with the pauses of its
    /// key, when the call began one. A call that its caller cancelled is not handed back, and
    /// counts as nothing.
    /// </summary>
    /// <param name="pass">The pass of the call.</param>
    /// <param name="result">What the call returned.</param>
    /// <param name="end">How the call ended, as the breakers count it.</param>
    public AiCallResult Leave(CallPass pass, AiCallResult result, CallEnd end)
    {
        var now = clock.GetUtcNow();
        var provider = pass.Provider;

        Change? ofProvider = null;
        Change? ofKey = null;
        var keysInTrouble = 0;

        lock (_gate)
        {
            switch (end)
            {
                case CallEnd.Healthy:
                    ofProvider = Close(_providers, provider);
                    ofKey = Close(_keys, (provider, pass.Key));
                    break;

                case CallEnd.Answered:
                    ofProvider = Close(_providers, provider);
                    break;

                case CallEnd.KeyRefused:
                    ofProvider = Close(_providers, provider);
                    ofKey = Count(_keys, (provider, pass.Key), pass, settings.AiKey.Failures, PauseOfKey, now);
                    break;

                case CallEnd.ProviderFault:
                    ofProvider = Count(_providers, provider, pass, settings.AiProvider.Failures, PauseOfProvider, now);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(end), end, null);
            }

            if (ofKey is { Paused: true })
            {
                keysInTrouble = _keys.Count(counted => counted.Key.Provider == provider && counted.Value.Trips > 0);
            }
        }

        // Said outside the gate. The provider is the one of the scope that the router began, and
        // is named here as well, for a reader of this event alone. A key is never named: whose
        // it is, is in the scope of the caller.
        if (ofProvider is { Paused: true } paused)
        {
            logger.LogWarning(
                "The calls to the AI provider {Provider} are paused until {PausedUntil}, after {Failures} failures in a row: pause {Trips} in a row",
                provider.Value,
                paused.Until,
                paused.Failures,
                paused.Trips);
        }
        else if (ofProvider is { Paused: false })
        {
            logger.LogInformation("The AI provider {Provider} answers again. Its calls are made as before", provider.Value);
        }

        if (ofKey is { Paused: true } refused)
        {
            logger.LogWarning(
                "The calls with a key for the AI provider {Provider} are paused until {PausedUntil}, after {Failures} refusals in a row: pause {Trips} in a row, with {KeysInTrouble} keys of the provider in trouble",
                provider.Value,
                refused.Until,
                refused.Failures,
                refused.Trips,
                keysInTrouble);

            return result with { KeyTrips = refused.Trips };
        }

        if (ofKey is { Paused: false })
        {
            logger.LogInformation("The AI provider {Provider} takes the calls with a key again that it refused", provider.Value);
        }

        return result;
    }

    public IReadOnlyList<AiCallPause> Pauses()
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            return
            [
                .. providers.ByCode.Keys
                    .Union(_providers.Keys)
                    .OrderBy(provider => provider.Value, StringComparer.Ordinal)
                    .Select(provider =>
                    {
                        _providers.TryGetValue(provider, out var breaker);
                        var keys = _keys.Where(counted => counted.Key.Provider == provider && counted.Value.Trips > 0).ToList();

                        return new AiCallPause(
                            provider,
                            breaker?.Failures ?? 0,
                            breaker?.Trips ?? 0,
                            breaker is not null && breaker.IsPaused(now) ? breaker.PausedUntil : null,
                            keys.Count,
                            keys.Count(counted => counted.Value.IsPaused(now)));
                    }),
            ];
        }
    }

    private TimeSpan PauseOfProvider(int trips) =>
        PauseOf(trips, settings.AiProvider.PauseSeconds, settings.AiProvider.LongestPauseSeconds);

    private TimeSpan PauseOfKey(int trips) => PauseOf(trips, settings.AiKey.PauseSeconds, settings.AiKey.LongestPauseSeconds);

    /// <summary>
    /// How long a pause lasts: the first as long as the file says, and each that follows another
    /// twice as long as the one before it, up to the longest.
    /// </summary>
    private static TimeSpan PauseOf(int trips, int firstSeconds, int longestSeconds)
    {
        // Past thirty doublings every pause is the longest, whatever the first was.
        var doublings = Math.Min(trips - 1, 30);
        var seconds = Math.Min((long)firstSeconds << doublings, longestSeconds);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Counts a failure, and begins a pause when it is one too many: the first after a pause, or
    /// as many in a row as the file says. Null when nothing changed.
    /// </summary>
    private Change? Count<TKey>(
        Dictionary<TKey, Breaker> breakers,
        TKey of,
        CallPass pass,
        int failuresBeforePause,
        Func<int, TimeSpan> pauseOf,
        DateTimeOffset now)
        where TKey : notnull
    {
        if (!breakers.TryGetValue(of, out var breaker))
        {
            if (breakers.Count >= MostKeys)
            {
                // The one that failed longest ago makes room.
                breakers.Remove(breakers.MinBy(counted => counted.Value.FailedAt).Key);
            }

            breakers[of] = breaker = new Breaker();
        }

        // A call that was under way when the pause began says nothing new: the pause is for
        // what it found.
        if (pass.Entered <= breaker.TrippedAtEntry)
        {
            return null;
        }

        breaker.Failures++;
        breaker.FailedAt = now;

        if (breaker.Trips == 0 && breaker.Failures < failuresBeforePause)
        {
            return null;
        }

        breaker.Trips++;
        breaker.PausedUntil = now + pauseOf(breaker.Trips);
        breaker.TrippedAtEntry = _entered;

        return new Change(Paused: true, breaker.PausedUntil, breaker.Failures, breaker.Trips);
    }

    /// <summary>
    /// Ends what a breaker counted, for an answer. Null when it had not paused: only the end of
    /// a pause is something to say.
    /// </summary>
    private static Change? Close<TKey>(Dictionary<TKey, Breaker> breakers, TKey of)
        where TKey : notnull
    {
        // One that counts nothing is not kept: a key that works is not known here.
        if (!breakers.Remove(of, out var breaker))
        {
            return null;
        }

        return breaker.Trips > 0 ? new Change(Paused: false, Until: null, Failures: 0, Trips: 0) : null;
    }

    private KeyMark MarkOf(string apiKey)
    {
        Span<byte> mark = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(apiKey), mark);

        return new KeyMark(BitConverter.ToUInt64(mark), BitConverter.ToUInt64(mark[sizeof(ulong)..]));
    }

    /// <summary>What a call changed of a breaker: a pause that began, or one that an answer ended.</summary>
    private sealed record Change(bool Paused, DateTimeOffset? Until, int Failures, int Trips);

    private sealed class Breaker
    {
        /// <summary>The failures in a row.</summary>
        public int Failures { get; set; }

        /// <summary>The pauses in a row, with no answer between them.</summary>
        public int Trips { get; set; }

        /// <summary>Until when the latest pause lasts, or lasted.</summary>
        public DateTimeOffset? PausedUntil { get; set; }

        /// <summary>When the latest failure was counted.</summary>
        public DateTimeOffset FailedAt { get; set; }

        /// <summary>How many calls had asked to pass when the latest pause began.</summary>
        public long TrippedAtEntry { get; set; }

        public bool IsPaused(DateTimeOffset now) => PausedUntil is { } until && now < until;
    }
}

/// <summary>A key, as the breakers know it: see the remarks of <see cref="AiCallBreakers"/>.</summary>
internal readonly record struct KeyMark(ulong First, ulong Second);

/// <summary>What a call is given when it asks the breakers whether it may be made.</summary>
/// <param name="Provider">The provider of the call.</param>
/// <param name="Key">The mark of its key.</param>
/// <param name="Entered">The place of the call among all that passed.</param>
/// <param name="Paused">
/// How the call ended when it may not be made: the calls to its provider are paused, or those
/// with its key. Null when it may be made.
/// </param>
internal readonly record struct CallPass(AiProviderCode Provider, KeyMark Key, long Entered, AiCallOutcome? Paused);
