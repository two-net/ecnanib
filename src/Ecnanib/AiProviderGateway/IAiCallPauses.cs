using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// Where the calls to the models are paused now: the gateway pauses the calls to a provider, and
/// the calls with one key for it, after failures in a row, and makes them again later
/// (NFR-REL-4). It is what tells trouble of one key from trouble of a provider, and what an
/// operator status screen shows. See docs/reliability.md.
/// </summary>
public interface IAiCallPauses
{
    /// <summary>
    /// How the calls to each provider of the configuration file stand now, in the order of the
    /// providers' codes. Each provider is counted on its own: one whose calls are paused leaves
    /// the others as they are.
    /// </summary>
    IReadOnlyList<AiCallPause> Pauses();
}

/// <summary>How the calls to one AI provider stand.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Failures">
/// How many calls to the provider in a row, of all users, ended with no answer or with a fault of
/// the provider.
/// </param>
/// <param name="Trips">
/// How many pauses in a row the calls to the provider have had, with no answer between them.
/// None when the provider answers.
/// </param>
/// <param name="PausedUntil">
/// Until when no call is made to the provider, or null when calls are made: also after a pause,
/// when they are made again to see whether the provider answers.
/// </param>
/// <param name="KeysInTrouble">
/// How many keys for the provider have had a pause of their own that no answer has ended yet:
/// the provider answers, and refuses the calls with those keys. Many at once are trouble of the
/// service or of the provider, and not of a key.
/// </param>
/// <param name="KeysPaused">How many of those keys no call is made with now.</param>
public sealed record AiCallPause(
    AiProviderCode Provider, int Failures, int Trips, DateTimeOffset? PausedUntil, int KeysInTrouble, int KeysPaused);
