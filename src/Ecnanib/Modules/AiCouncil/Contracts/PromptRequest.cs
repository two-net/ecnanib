using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// What one role's prompt is composed for: the combination that picks its templates (FR-PRM-02), the agent's
/// custom instructions (FR-PRM-05) and the runtime context (FR-PRM-01). Its ToString shows no text, because custom
/// instructions are the user's and not even administrators may read them (FR-ADM-01, FR-LOG-07).
/// </summary>
public sealed record PromptRequest(AiRole Role, Market Market, Timeframe Timeframe, PromptContext Context)
{
    private readonly string? _customInstructions;

    public PromptContext Context { get; init; } = Context ?? throw new ArgumentNullException(nameof(Context));

    /// <summary>
    /// The agent's custom instructions (FR-PRM-05), at most <see cref="PromptLimits.CustomInstructionsMaxLength"/>
    /// characters. Agents refuses longer ones on save, and this refuses them again. Null or blank adds nothing.
    /// </summary>
    public string? CustomInstructions
    {
        get => _customInstructions;
        init => _customInstructions = value is { Length: > PromptLimits.CustomInstructionsMaxLength }
            ? throw new ArgumentException(
                $"Custom instructions are at most {PromptLimits.CustomInstructionsMaxLength} characters.", nameof(CustomInstructions))
            : value;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PromptRequest)} for {Role}, {Market}, {Timeframe}");
}

/// <summary>
/// The runtime context, FR-PRM-01's last layer. Each role gets what it needs, so everything is optional. What
/// Ecnanib computed goes into the prompt as data, and what a person or a model wrote goes in as untrusted content
/// (FR-PRM-06). Amounts are in the quote asset and quantities in the base asset. Its ToString shows no text.
/// </summary>
public sealed record PromptContext
{
    public static PromptContext Empty { get; } = new();

    /// <summary>
    /// The cycle's market data and indicators, for the request's market and timeframe. How many candles go in
    /// depends on the role: see <see cref="PromptWindows"/>.
    /// </summary>
    public MarketSnapshot? Snapshot { get; init; }

    public PromptAccount? Account { get; init; }

    /// <summary>The agent's own open position, or null when it has none.</summary>
    public PromptPosition? Position { get; init; }

    /// <summary>The agent's entry order that hasn't filled, or null when it has none.</summary>
    public PromptPendingEntry? PendingEntry { get; init; }

    /// <summary>The agent's recent results, which the loss limits count.</summary>
    public TradingRecord? Record { get; init; }

    /// <summary>The agent's risk settings after <see cref="RiskCaps.Apply"/>, so the prompt shows the limits that hold.</summary>
    public RiskSettings? Risk { get; init; }

    /// <summary>The most recent relevant reflections (FR-EXP-04), newest first. A model wrote them, so they're untrusted.</summary>
    public IReadOnlyList<PromptReflection> Reflections { get; init; } = [];

    /// <summary>
    /// News, social posts, web results and earlier roles' output, in the order given (FR-PRM-06). Earlier roles'
    /// output is here because a model can repeat what it read.
    /// </summary>
    public IReadOnlyList<UntrustedContent> External { get; init; } = [];

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{nameof(PromptContext)}, {Reflections.Count} reflections, {External.Count} untrusted items");
}

/// <summary>The agent's account, as far as it concerns the agent.</summary>
/// <param name="CapitalAllocation">The most the agent may commit (FR-AGT-02).</param>
/// <param name="AvailableQuote">Spot: the free balance of the quote asset. Futures: the available margin balance.</param>
public sealed record PromptAccount(string BaseAsset, string QuoteAsset, decimal CapitalAllocation, decimal AvailableQuote)
{
    public string BaseAsset { get; init; } = !string.IsNullOrWhiteSpace(BaseAsset)
        ? BaseAsset
        : throw new ArgumentException("An account needs its base asset.", nameof(BaseAsset));

    public string QuoteAsset { get; init; } = !string.IsNullOrWhiteSpace(QuoteAsset)
        ? QuoteAsset
        : throw new ArgumentException("An account needs its quote asset.", nameof(QuoteAsset));

    /// <summary>Spot only: what the agent's own fills bought, which is all it may sell (FR-AGT-09).</summary>
    public decimal? BaseHeld { get; init; }

    /// <summary>Futures only: the agent's leverage after the caps.</summary>
    public int? Leverage { get; init; }
}

/// <param name="AverageEntry">The average entry price.</param>
public sealed record PromptPosition(PositionSide Side, decimal Quantity, decimal AverageEntry, DateTimeOffset OpenedAt)
{
    /// <summary>The protective stop's trigger price.</summary>
    public decimal? Stop { get; init; }

    public IReadOnlyList<decimal> Targets { get; init; } = [];

    public decimal? UnrealizedPnl { get; init; }

    /// <summary>Futures only, when Binance gives one.</summary>
    public decimal? LiquidationPrice { get; init; }
}

/// <param name="Price">Null for a market order.</param>
public sealed record PromptPendingEntry(PositionSide Side, EntryType Type, decimal? Price, decimal Quantity, DateTimeOffset PlacedAt);

/// <summary>A lesson from a closed trade (FR-EXP-04). A model wrote it, so it's untrusted. Its ToString shows no text.</summary>
/// <param name="RMultiple">The trade's result in multiples of what it risked.</param>
public sealed record PromptReflection(DateTimeOffset TradeClosedAt, decimal? RMultiple, string Lesson)
{
    public string Lesson { get; init; } = Lesson ?? throw new ArgumentNullException(nameof(Lesson));

    public IReadOnlyList<string> Tags { get; init; } = [];

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PromptReflection)}, {Lesson.Length} characters");
}

/// <summary>Where untrusted content came from (FR-PRM-06). Its name goes into the prompt.</summary>
public enum UntrustedKind
{
    News,
    Social,
    Web,

    /// <summary>An earlier role's output, such as the analysts' for the Strategist.</summary>
    RoleOutput,
}

/// <summary>
/// Text from outside Ecnanib, or from a model, which a prompt delimits and labels as data and tells the model never
/// to obey (FR-PRM-06, NFR-SEC-08). Its ToString shows no text.
/// </summary>
/// <param name="Source">Who published it, such as a site, an account or a role.</param>
public sealed record UntrustedContent(UntrustedKind Kind, string Source, string Text)
{
    public UntrustedKind Kind { get; init; } = Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Not a kind of untrusted content.");

    public string Source { get; init; } = Source ?? throw new ArgumentNullException(nameof(Source));

    public string Text { get; init; } = Text ?? throw new ArgumentNullException(nameof(Text));

    public string? Title { get; init; }

    public string? Url { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(UntrustedContent)} {Kind}, {Text.Length} characters");
}
