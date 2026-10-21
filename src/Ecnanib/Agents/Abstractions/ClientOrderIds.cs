using System.Security.Cryptography;
using System.Text;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// Deterministic client order ids (FR-EXE-07, NFR-REL-02): the same agent, scope, purpose and sequence always give
/// the same id, so a retry adopts the order it placed before instead of placing another. The id is the prefix and 31
/// hex characters of a SHA-256, 32 characters within Binance's 36 and its character set.
/// </summary>
public static class ClientOrderIds
{
    public const string PaperPrefix = "p";

    public const string LivePrefix = "e";

    public const string Entry = "entry";

    public const string StopLoss = "sl";

    public const string TakeProfit = "tp";

    public const string Close = "close";

    public const string PartialClose = "partial";

    public const string OcoList = "oco";

    public static string For(string prefix, Guid agentId, Guid scope, string purpose, int sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId:N}|{scope:N}|{purpose}|{sequence}"));
        return prefix + Convert.ToHexStringLower(hash)[..31];
    }
}
