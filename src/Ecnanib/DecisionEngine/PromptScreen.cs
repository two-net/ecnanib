using System.Buffers;
using System.Text.RegularExpressions;
using Ecnanib.Platform.Logging;

namespace Ecnanib.DecisionEngine;

/// <summary>What a prompt never holds, as the screen of a prompt finds it (FR-PRM-9).</summary>
public enum PromptLeak
{
    /// <summary>A secret: a value from the configuration file, or a key or a token by its format.</summary>
    Secret,

    /// <summary>An email address.</summary>
    EmailAddress,

    /// <summary>An identifier of an account, an agent, or a key, each of which is a UUID.</summary>
    Identifier,
}

/// <summary>
/// Says whether a text holds what a prompt never holds: a secret, an email address, or an
/// account identifier (FR-PRM-9). Every part of a prompt passes it before it is placed, so what
/// a provider is sent holds none (NFR-SEC-8). See docs/prompts.md.
/// </summary>
/// <remarks>
/// <para>
/// It goes by the shape of a value, and by nothing that stands beside it: a finding refuses a
/// prompt, so ordinary prose about a market must never be one. A handle such as <c>@name</c>,
/// the hash of a transaction, and the address of a wallet pass.
/// </para>
/// <para>
/// What has no shape, it cannot find: a name, or a key in a format that it does not know. That
/// is kept out by what a caller can hand over at all, which <c>PromptRules</c> of the tests pins.
/// </para>
/// <para>
/// The patterns do not backtrack. Part of what is screened was written by a model that read the
/// web, and no text can make a match take longer than its length allows.
/// </para>
/// </remarks>
internal sealed partial class PromptScreen(SecretMask secrets)
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>What <paramref name="text"/> holds that a prompt never holds, or null when it holds none.</summary>
    public PromptLeak? LeakIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (secrets.Holds(text) || HoldsExchangeKey(text))
        {
            return PromptLeak.Secret;
        }

        if (EmailAddress().IsMatch(text))
        {
            return PromptLeak.EmailAddress;
        }

        return Uuid().IsMatch(text) ? PromptLeak.Identifier : null;
    }

    /// <summary>
    /// Whether a text holds a key in the format Binance issues: 64 letters and digits. The hash
    /// of a block or of a transaction is as long, and is hexadecimal: it is no key, and passes.
    /// </summary>
    private static bool HoldsExchangeKey(string text)
    {
        foreach (var run in SixtyFourLettersAndDigits().EnumerateMatches(text))
        {
            if (text.AsSpan(run.Index, run.Length).ContainsAnyExcept(HexDigits))
            {
                return true;
            }
        }

        return false;
    }

    // ada@example.com. A handle, @ada, has nothing before the sign, and a price, 0.5@61000, no
    // name of a domain after it.
    [GeneratedRegex(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    // 019a7c3e-5b1f-7d2a-9c4e-1f2a3b4c5d6e, as an id is written.
    [GeneratedRegex(
        @"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    // A run of exactly 64: a longer one, such as a hash behind 0x, is not found.
    [GeneratedRegex(@"\b[A-Za-z0-9]{64}\b", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex SixtyFourLettersAndDigits();
}
