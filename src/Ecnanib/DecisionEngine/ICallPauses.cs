using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Where the calls to the models are paused now: the calls to a provider, of all users, and the
/// calls with single keys for it, each after failures in a row (NFR-REL-4). It is what an
/// operator status screen shows. See "The breakers of the AI providers" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// The AI Provider Gateway keeps it, and the pages reach the AI providers only through other
/// components: so the Decision Engine, whose calls it is about, says it on.
/// </para>
/// <para>
/// It is of every user, and names none: it holds the codes of the providers, numbers, and a
/// time. Of a key it says only that there is one that a provider refuses, and never whose.
/// </para>
/// <para>
/// The gateway keeps it in memory, so this is what the instance that is asked says of itself.
/// </para>
/// </remarks>
public interface ICallPauses
{
    /// <summary>How the calls to each provider of the configuration file stand now, in the order of the providers' codes.</summary>
    IReadOnlyList<CallPause> Now();
}

/// <summary>How the calls to the models of one AI provider stand.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Failures">How many calls to it in a row, of all users, ended with no answer or with a fault of the provider.</param>
/// <param name="Trips">How many pauses in a row its calls have had, with no answer between them: none when it answers.</param>
/// <param name="PausedUntil">Until when no call is made to it, or null when calls are made.</param>
/// <param name="KeysInTrouble">How many keys for it have had a pause of their own that no answer has ended yet.</param>
/// <param name="KeysPaused">How many of those keys no call is made with now.</param>
public sealed record CallPause(
    AiProviderCode Provider, int Failures, int Trips, DateTimeOffset? PausedUntil, int KeysInTrouble, int KeysPaused);

/// <summary>Says on what the gateway says. See <see cref="ICallPauses"/>.</summary>
internal sealed class CallPauses(IAiCallPauses pauses) : ICallPauses
{
    public IReadOnlyList<CallPause> Now() =>
    [
        .. pauses.Pauses().Select(pause => new CallPause(
            pause.Provider, pause.Failures, pause.Trips, pause.PausedUntil, pause.KeysInTrouble, pause.KeysPaused)),
    ];
}
