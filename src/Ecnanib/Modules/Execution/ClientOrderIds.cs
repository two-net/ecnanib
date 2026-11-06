using System.Globalization;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Client order IDs, derived from the agent, the cycle and the order leg (FR-EXE-04): <c>e</c>, the agent's ID in
/// base64url (22 characters), the cycle's candle close in Unix minutes in base 36 (6), then the leg, the protection
/// and the attempt (one each). That's 32 characters, within Binance's 36, in the characters both markets accept. An
/// agent has one cycle per candle, so no two orders share an ID, and an ID sent again after it was proven missing is
/// the same order. Every ID of an agent starts with <see cref="AgentPrefix"/>, which is how its orders are told
/// from the user's own (FR-AGT-09). See Order execution in docs/architecture.md.
/// </summary>
internal static class ClientOrderIds
{
    public const int Length = 32;

    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    private const int CloseLength = 6;

    /// <summary>The highest protection sequence or attempt an ID can hold.</summary>
    public const int MaximumIndex = 35;

    /// <summary>What every client order ID of the agent's starts with: 23 characters.</summary>
    public static string AgentPrefix(Guid agentId)
    {
        Span<byte> bytes = stackalloc byte[16];
        agentId.TryWriteBytes(bytes);

        return "e" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Entry(Guid agentId, DateTimeOffset candleClose) => Of(agentId, candleClose, 'E', 0, 0);

    public static string Stop(Guid agentId, DateTimeOffset candleClose, int protection, int attempt) =>
        Of(agentId, candleClose, 'S', protection, attempt);

    public static string Target(Guid agentId, DateTimeOffset candleClose, int protection, int attempt) =>
        Of(agentId, candleClose, 'T', protection, attempt);

    /// <summary>A Spot OCO list's own ID. Its stop and target have the IDs <see cref="Stop"/> and <see cref="Target"/> give.</summary>
    public static string List(Guid agentId, DateTimeOffset candleClose, int protection, int attempt) =>
        Of(agentId, candleClose, 'L', protection, attempt);

    public static string Close(Guid agentId, DateTimeOffset candleClose, int protection, int attempt) =>
        Of(agentId, candleClose, 'X', protection, attempt);

    private static string Of(Guid agentId, DateTimeOffset candleClose, char leg, int protection, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(protection);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(protection, MaximumIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attempt, MaximumIndex);

        var minutes = candleClose.ToUnixTimeSeconds() / 60;
        ArgumentOutOfRangeException.ThrowIfNegative(minutes, nameof(candleClose));

        return string.Create(CultureInfo.InvariantCulture, $"{AgentPrefix(agentId)}{Base36(minutes)}{leg}{Digits[protection]}{Digits[attempt]}");
    }

    private static string Base36(long value)
    {
        Span<char> digits = stackalloc char[CloseLength];

        for (var index = CloseLength - 1; index >= 0; index--)
        {
            digits[index] = Digits[(int)(value % 36)];
            value /= 36;
        }

        if (value != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A candle close that far ahead doesn't fit.");
        }

        return new string(digits);
    }
}
