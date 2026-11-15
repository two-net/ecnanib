using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>
/// What differs between providers in the list of their models: where it is, how the key travels,
/// how a page of it reads and names the page after it, and which answer is the provider's answer
/// to a wrong key. Each provider's list says those, and <see cref="ModelListCall"/> does the
/// rest, which is the same for all.
/// </summary>
internal interface IModelListFormat
{
    /// <summary>The provider, whose address the configuration file holds.</summary>
    AiProviderCode Provider { get; }

    /// <summary>The path of a page of the list, under the provider's address, with its query.</summary>
    /// <param name="next">What the page before it named as the next one, or null for the first page.</param>
    string PathOf(string? next);

    /// <summary>Puts the key into the headers of the request, as the provider takes it.</summary>
    void Authorize(HttpRequestHeaders headers, string apiKey);

    /// <summary>
    /// Reads a page that came with a success status, or says null when it is not what the
    /// provider sends: no list, or a model without an identifier.
    /// </summary>
    ModelListPage? Read(JsonElement answer);

    /// <summary>Whether an answer with an error status is the provider's answer to a wrong key.</summary>
    /// <param name="status">The status of the answer.</param>
    /// <param name="error">The body of the answer, or null when it is not a JSON object.</param>
    bool RefusesKey(HttpStatusCode status, JsonElement? error);
}

/// <summary>A page of a provider's list of models, read.</summary>
/// <param name="Models">The models of the page.</param>
/// <param name="Next">What names the page after it, or null when this is the last one.</param>
internal sealed record ModelListPage(IReadOnlyList<ListedAiModel> Models, string? Next);

/// <summary>
/// The one exchange that every provider's list of models makes: GETs to the provider's address of
/// the configuration file, with the key in a header, page after page, within one time limit
/// (FR-CAT-1, NFR-PRF-4). What differs between providers each list says, as an
/// <see cref="IModelListFormat"/>. See docs/ai-providers.md.
/// </summary>
/// <remarks>
/// <para>
/// A list is whole or it is none. A page that fails, an answer that is not the provider's list, a
/// model without an identifier, and a list with no model at all end as
/// <see cref="AiModelListOutcome.Failed"/>: the catalog counts a model that a list lacks as
/// missing, so a part of a list would make models look gone.
/// </para>
/// <para>
/// What happens on the network leaves here as an <see cref="AiModelListOutcome"/>, and never as
/// an error of the network. Only a caller that cancels ends the call with an error. The key is
/// put into the headers of each request and is kept nowhere else, and nothing a provider writes
/// reaches a log event from here.
/// </para>
/// </remarks>
internal sealed class ModelListCall(
    IHttpClientFactory clients,
    AiProvidersSettings settings,
    TimeProvider clock,
    ILogger<ModelListCall> logger)
{
    /// <summary>
    /// How long a provider may take to list its models, all pages together. It leaves the catalog
    /// the time to store them within the minute that a sync may take (NFR-PRF-4).
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    // The most pages a list is read for. A provider sends its models in one or two: more than
    // this is a list that does not end.
    private const int MostPages = 50;

    /// <summary>Asks a provider for its models, and says how that ended.</summary>
    /// <param name="format">The provider's own way of listing.</param>
    /// <param name="apiKey">The key the list is asked with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="InvalidOperationException">The configuration file has no entry for the provider.</exception>
    public async Task<AiModelList> AskAsync(IModelListFormat format, string apiKey, CancellationToken cancellationToken)
    {
        if (!settings.ByCode.TryGetValue(format.Provider, out var endpoint))
        {
            throw new InvalidOperationException($"The configuration file has no entry for AI provider '{format.Provider}'.");
        }

        // The one limit on the list: the client of the gateway has none of its own. It runs on
        // the application's clock, and covers every page.
        using var limit = new CancellationTokenSource(Limit, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        var models = new List<ListedAiModel>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var asked = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;

        try
        {
            var client = clients.CreateClient(AiProviderGatewayModule.HttpClientName);

            for (var page = 0; page < MostPages; page++)
            {
                using var message = new HttpRequestMessage(
                    HttpMethod.Get, $"{endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}/{format.PathOf(next)}");
                format.Authorize(message.Headers, apiKey);

                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, either.Token);
                var body = await response.ReadObjectAsync(either.Token);

                if (!response.IsSuccessStatusCode)
                {
                    return Without(
                        format.RefusesKey(response.StatusCode, body) ? AiModelListOutcome.AuthenticationFailed : AiModelListOutcome.Failed);
                }

                // A success that is not the provider's list: the page of something in between, or
                // an answer of a kind that this code does not know.
                if (body is not { } answer || format.Read(answer) is not { } read)
                {
                    return NoList("NotAList");
                }

                // A model that two pages name, because the list changed between them, is one model.
                models.AddRange(read.Models.Where(model => listed.Add(model.Id)));

                if (read.Next is null)
                {
                    return models.Count == 0 ? NoList("NoModels") : new AiModelList(AiModelListOutcome.Listed, models);
                }

                // A page that names one that was asked for already would be read forever.
                if (!asked.Add(read.Next))
                {
                    return NoList("PagesRepeat");
                }

                next = read.Next;
            }

            return NoList("TooManyPages");
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log of outbound calls, with why.
            return Without(AiModelListOutcome.Failed);
        }
        catch (IOException)
        {
            // The answer broke off while it was read.
            return Without(AiModelListOutcome.Failed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider took longer than the limit. The caller did not cancel.
            return Without(AiModelListOutcome.TimedOut);
        }
    }

    private static AiModelList Without(AiModelListOutcome outcome) => new(outcome, []);

    /// <summary>
    /// An answer whose status says that the call went well, so the log of outbound calls does
    /// too, and that is no list all the same. This says why, in a word of this code's own.
    /// </summary>
    private AiModelList NoList(string reason)
    {
        logger.LogWarning("The answer of the provider is not the list of its models: {Reason}", reason);

        return Without(AiModelListOutcome.Failed);
    }
}

/// <summary>
/// Reads what a provider says about a model in its list. A value that is missing, or that is not
/// what it should be, is null, which is unknown (FR-CAT-4).
/// </summary>
internal static class ListedValues
{
    /// <summary>A number of tokens, or null when the member is missing or holds no number above zero.</summary>
    public static int? Tokens(this JsonElement model, string name) =>
        model.Member(name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count) && count > 0
            ? count
            : null;

    /// <summary>
    /// A moment in time, in UTC and to the microsecond, which is what the database keeps. The
    /// member holds it as text, in the form of ISO 8601, with a date alone as the start of that
    /// day in UTC, or as the seconds since 1970. Null when it is missing, is in another form, or
    /// is no later than 1970: that is how a provider writes a date it does not know.
    /// </summary>
    /// <remarks>
    /// Text in another form is not read at all. A looser reading would make a date out of a part
    /// of one, by filling in the rest from today, and that is a guess.
    /// </remarks>
    public static DateTimeOffset? Moment(this JsonElement model, string name)
    {
        DateTimeOffset moment;

        switch (model.Member(name))
        {
            // With an offset, or as UTC, it is that moment. Without either, it is taken as UTC.
            case { ValueKind: JsonValueKind.String } text when text.TryGetDateTime(out var written):
                moment = written.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(written, TimeSpan.Zero)
                    : new DateTimeOffset(written.ToUniversalTime());
                break;

            case { ValueKind: JsonValueKind.Number } number
                when number.TryGetInt64(out var seconds)
                    && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds():
                moment = seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UnixEpoch;
                break;

            default:
                return null;
        }

        if (moment <= DateTimeOffset.UnixEpoch)
        {
            return null;
        }

        var ticks = moment.UtcTicks;

        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }
}
