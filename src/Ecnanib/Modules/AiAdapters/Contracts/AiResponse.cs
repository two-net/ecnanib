using System.Globalization;

namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// A model's answer. Its ToString shows no response text, so logging it by mistake can't put one in a log
/// (FR-LOG-07). The decision trace keeps the text.
/// </summary>
/// <param name="Text">What the model wrote. JSON when the request had a schema, which the council still validates.</param>
/// <param name="Usage">Every attempt's tokens added up, including refused ones, because each was billed.</param>
/// <param name="Cost">Every attempt's cost in US dollars, or null when the request had no price.</param>
/// <param name="Truncated">The model hit the request's MaxOutputTokens, so the text is incomplete.</param>
/// <param name="Attempts">How many times it was sent, at least 1.</param>
/// <param name="Latency">From the first send to this answer, including retries and the waits between them.</param>
public sealed record AiResponse(string Text, AiUsage Usage, decimal? Cost, bool Truncated, int Attempts, TimeSpan Latency)
{
    public override string ToString() =>
        $"{nameof(AiResponse)}, {Text.Length.ToString(CultureInfo.InvariantCulture)} characters, {Usage}, {Attempts.ToString(CultureInfo.InvariantCulture)} attempts";
}

/// <summary>
/// Tokens as the provider billed them (EXT-AI-02). Adapters count reasoning or thinking tokens as output,
/// because providers bill them as output.
/// </summary>
public sealed record AiUsage(long InputTokens, long OutputTokens)
{
    public static AiUsage Zero { get; } = new(0, 0);

    public long InputTokens { get; init; } = InputTokens >= 0
        ? InputTokens
        : throw new ArgumentOutOfRangeException(nameof(InputTokens), "A token count can't be negative.");

    public long OutputTokens { get; init; } = OutputTokens >= 0
        ? OutputTokens
        : throw new ArgumentOutOfRangeException(nameof(OutputTokens), "A token count can't be negative.");

    public static AiUsage operator +(AiUsage left, AiUsage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return new AiUsage(left.InputTokens + right.InputTokens, left.OutputTokens + right.OutputTokens);
    }

    public override string ToString() =>
        $"{InputTokens.ToString(CultureInfo.InvariantCulture)} input and {OutputTokens.ToString(CultureInfo.InvariantCulture)} output tokens";
}
