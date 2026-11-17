using System.Text.Encodings.Web;
using System.Text.Json;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The report of the Sentiment Analyst, as the service hands it to the Strategist: what the
/// model answered, marked with whether it rests on live data (FR-CYC-3). See "Live search" in
/// docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mark is the service's, and not the model's.</b> A report rests on live data when its
/// call offered the model a live search and the provider ran at least one. Every other report is
/// marked <see cref="NoLiveData"/>: that of a model that cannot search live, and that of a model
/// that could and did not. What the report says of itself changes nothing of the mark, so
/// content that a search found cannot make a report count for more than it does (FR-PRM-8).
/// </para>
/// <para>
/// The instructions of the Strategist say what to do with the mark: a report without live data
/// is given no weight. The mark is a fact in the data package, and the instruction is in the
/// template, because a role follows no instruction that it finds in the data package.
/// </para>
/// <para>
/// A report is made only by <see cref="Of"/>, from a call and its result, so nothing else can
/// say that a report rests on live data.
/// </para>
/// </remarks>
public sealed record SentimentReport
{
    /// <summary>How a report is marked that rests on no live data, also in the instructions of the Strategist.</summary>
    public const string NoLiveData = "no live data";

    /// <summary>How a report is marked that rests on what a live search found.</summary>
    public const string WithLiveData = "live data";

    /// <summary>What the line of the mark begins with, in the data package of a later role.</summary>
    public const string Heading = "Sentiment report: ";

    // Every character as it is, but those that JSON itself escapes: a line break inside a text
    // is written as two characters, so the report is one line whatever a model wrote.
    private static readonly JsonSerializerOptions OneLine = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private SentimentReport(string resultJson, bool liveData)
    {
        ResultJson = resultJson;
        LiveData = liveData;
    }

    /// <summary>What the Sentiment Analyst answered: one JSON object, on one line.</summary>
    public string ResultJson { get; }

    /// <summary>Whether the report rests on live data: its call offered a live search, and the provider ran one.</summary>
    public bool LiveData { get; }

    /// <summary>The mark of the report: <see cref="WithLiveData"/> or <see cref="NoLiveData"/>.</summary>
    public string Mark => LiveData ? WithLiveData : NoLiveData;

    /// <summary>
    /// The report as a later role reads it in its data package: the line of the mark, and under
    /// it the report on one line. No line of it begins as a heading of a prompt does, whatever
    /// the model wrote, so a report cannot pose as a part of the prompt.
    /// </summary>
    public string Text => $"{Heading}{Mark}\n{ResultJson}";

    /// <summary>The report of a call of the Sentiment Analyst that succeeded.</summary>
    /// <param name="call">The call, which says whether the model was offered a live search.</param>
    /// <param name="result">What the gateway answered the call with, which says how many searches the provider ran.</param>
    /// <exception cref="ArgumentException">
    /// The call is not one of the Sentiment Analyst, or it did not succeed. What becomes of a
    /// cycle whose Sentiment Analyst gave no result is for what runs the roles to decide
    /// (FR-TEAM-9).
    /// </exception>
    public static SentimentReport Of(RoleCall call, AiCallResult result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);

        if (call.Role.Role != Role.SentimentAnalyst)
        {
            throw new ArgumentException($"A sentiment report is of the Sentiment Analyst, and not of the {call.Role.Role}.", nameof(call));
        }

        if (result.Outcome != AiCallOutcome.Succeeded || result.ResultJson is not { } answered)
        {
            throw new ArgumentException($"A call that ended as {result.Outcome} gave no report.", nameof(result));
        }

        using var report = JsonDocument.Parse(answered);

        return new SentimentReport(
            JsonSerializer.Serialize(report.RootElement, OneLine),
            liveData: call.Request.LiveSearch && result.LiveSearches > 0);
    }
}
