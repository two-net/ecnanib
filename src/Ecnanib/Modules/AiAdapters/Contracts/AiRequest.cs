using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// One request to a model, in a form every provider can take (EXT-AI-05). Each adapter turns it into its
/// provider's own. It checks itself as it's built, so a bad value throws here rather than at a provider.
/// Its ToString shows no prompt, so logging it by mistake can't put one in a log (FR-LOG-07).
/// </summary>
/// <param name="Model">The provider's model ID, from the catalog.</param>
/// <param name="Instructions">The system layers of the prompt (FR-PRM-01), which providers keep apart from the messages.</param>
/// <param name="Messages">The conversation, starting with the user. More than one turn lets a response that broke its output contract be sent back with the errors (FR-CYC-05).</param>
/// <param name="MaxOutputTokens">The most the model may write, which bounds the cost of the call.</param>
public sealed record AiRequest(string Model, AiRole Role, string Instructions, IReadOnlyList<AiMessage> Messages, int MaxOutputTokens)
{
    public string Model { get; init; } = !string.IsNullOrWhiteSpace(Model)
        ? Model
        : throw new ArgumentException("A model is required.", nameof(Model));

    public string Instructions { get; init; } = Instructions ?? throw new ArgumentNullException(nameof(Instructions));

    public IReadOnlyList<AiMessage> Messages { get; init; } = Messages is [{ Speaker: AiSpeaker.User }, ..]
        ? Messages
        : throw new ArgumentException("A request needs at least one message, and the first is the user's.", nameof(Messages));

    public int MaxOutputTokens { get; init; } = MaxOutputTokens > 0
        ? MaxOutputTokens
        : throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens), "A request must allow at least one output token.");

    /// <summary>
    /// The shape the answer must have, sent as the provider's own structured output where it has one
    /// (FR-PRM-08). The council still validates the answer, because not every provider enforces it.
    /// </summary>
    public AiOutputSchema? Schema { get; init; }

    /// <summary>Lets the model use the provider's own live web or X search, where it has one (EXT-AI-02).</summary>
    public bool LiveSearch { get; init; }

    /// <summary>The model's price from the catalog, or null when it isn't known, in which case the call has no cost.</summary>
    public AiModelPrice? Price { get; init; }

    public override string ToString() =>
        $"{nameof(AiRequest)} {Model} for {Role}, {Messages.Count.ToString(CultureInfo.InvariantCulture)} messages";
}

public enum AiSpeaker
{
    User,
    Assistant,
}

/// <summary>One turn of the conversation. Its ToString shows no content.</summary>
/// <param name="Content">Text and images from the user. The assistant's turns are text only, because no provider takes an image from the model.</param>
public sealed record AiMessage(AiSpeaker Speaker, IReadOnlyList<AiContent> Content)
{
    public IReadOnlyList<AiContent> Content { get; init; } = Content switch
    {
        null or { Count: 0 } => throw new ArgumentException("A message needs some content.", nameof(Content)),
        _ when Content.Any(part => part is null) => throw new ArgumentException("A message's parts can't be null.", nameof(Content)),
        _ when Speaker == AiSpeaker.Assistant && Content.Any(part => part is not AiText) =>
            throw new ArgumentException("The assistant's turn is text only, because no provider takes an image from the model.", nameof(Content)),
        _ => Content,
    };

    public static AiMessage User(params AiContent[] content) => new(AiSpeaker.User, content);

    public static AiMessage User(string text) => User(new AiText(text));

    public static AiMessage Assistant(string text) => new(AiSpeaker.Assistant, [new AiText(text)]);

    public override string ToString() =>
        $"{nameof(AiMessage)} from {Speaker}, {Content.Count.ToString(CultureInfo.InvariantCulture)} parts";
}

/// <summary>Part of a message: <see cref="AiText"/> or <see cref="AiImage"/>.</summary>
public abstract record AiContent;

/// <summary>Text, which never appears in its ToString.</summary>
public sealed record AiText(string Text) : AiContent
{
    public string Text { get; init; } = Text ?? throw new ArgumentNullException(nameof(Text));

    public override string ToString() => $"{nameof(AiText)}, {Text.Length.ToString(CultureInfo.InvariantCulture)} characters";
}

/// <summary>An image, such as a rendered chart for the Technical Analyst (EXT-AI-02).</summary>
/// <param name="MediaType">image/png or image/jpeg, the only ones every provider takes. xAI documents no others.</param>
public sealed record AiImage(string MediaType, ReadOnlyMemory<byte> Data) : AiContent
{
    private static readonly string[] MediaTypes = ["image/png", "image/jpeg"];

    public string MediaType { get; init; } = MediaTypes.Contains(MediaType, StringComparer.Ordinal)
        ? MediaType
        : throw new ArgumentException($"An image must be one of {string.Join(", ", MediaTypes)}.", nameof(MediaType));

    public ReadOnlyMemory<byte> Data { get; init; } = Data.Length > 0
        ? Data
        : throw new ArgumentException("An image needs its bytes.", nameof(Data));

    public override string ToString() =>
        $"{nameof(AiImage)} {MediaType}, {Data.Length.ToString(CultureInfo.InvariantCulture)} bytes";
}

/// <summary>A JSON Schema that the answer must match (Appendix B).</summary>
/// <param name="Name">Letters, digits, underscores and hyphens, up to 64, which every provider accepts.</param>
/// <param name="Schema">A JSON Schema object.</param>
public sealed partial record AiOutputSchema(string Name, JsonElement Schema)
{
    public string Name { get; init; } = Name is not null && NamePattern().IsMatch(Name)
        ? Name
        : throw new ArgumentException("A schema's name is 1 to 64 letters, digits, underscores or hyphens.", nameof(Name));

    public JsonElement Schema { get; init; } = Schema.ValueKind == JsonValueKind.Object
        ? Schema.Clone()
        : throw new ArgumentException("A schema is a JSON object.", nameof(Schema));

    /// <exception cref="JsonException"><paramref name="json"/> isn't JSON.</exception>
    public static AiOutputSchema Parse(string name, string json)
    {
        using var document = JsonDocument.Parse(json);

        return new AiOutputSchema(name, document.RootElement);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>A model's price in US dollars per million tokens, which is how providers price them (FR-CAT-01).</summary>
public sealed record AiModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal InputPerMillion { get; init; } = InputPerMillion >= 0
        ? InputPerMillion
        : throw new ArgumentOutOfRangeException(nameof(InputPerMillion), "A price can't be negative.");

    public decimal OutputPerMillion { get; init; } = OutputPerMillion >= 0
        ? OutputPerMillion
        : throw new ArgumentOutOfRangeException(nameof(OutputPerMillion), "A price can't be negative.");

    /// <summary>
    /// What <paramref name="usage"/> costs, in US dollars, in decimal throughout (DATA-R-02). It's an
    /// estimate, because providers charge less for cached input and extra for searches.
    /// </summary>
    public decimal CostOf(AiUsage usage) =>
        ((usage.InputTokens * InputPerMillion) + (usage.OutputTokens * OutputPerMillion)) / 1_000_000m;
}
