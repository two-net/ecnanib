using System.Globalization;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// A stored layer of a prompt (FR-PRM-01), in the order the layers are composed. FR-PRM-01's system layer is
/// <see cref="System"/> and <see cref="OutputContract"/> together. Custom instructions and runtime context, its
/// layers 5 and 6, aren't stored: they come from the agent and the cycle. Rows store the name, so a value is never
/// renamed. See Prompts in docs/architecture.md.
/// </summary>
public enum PromptLayer
{
    /// <summary>Safety rules and how to treat untrusted content. Not editable by users.</summary>
    System,

    /// <summary>The role's output contract (Appendix B), which completes the system layer. Keyed by role.</summary>
    OutputContract,

    /// <summary>The role template. Keyed by role.</summary>
    Role,

    /// <summary>The Spot or Futures overlay (Appendix A.2). Keyed by market.</summary>
    Market,

    /// <summary>The 15m, 1H, 4H or 1D overlay (Appendix A.1). Keyed by timeframe.</summary>
    Timeframe,
}

/// <summary>
/// Which combinations a template applies to: its layer, plus any of role, market and timeframe. A layer's own
/// dimension is required: a role for <see cref="PromptLayer.Role"/> and <see cref="PromptLayer.OutputContract"/>,
/// a market for <see cref="PromptLayer.Market"/>, and a timeframe for <see cref="PromptLayer.Timeframe"/>. Any
/// other dimension narrows it, which is how an administrator overrides a combination (FR-PRM-02): for each layer,
/// the matching template with the most dimensions wins, and between two with as many, role beats market, which
/// beats timeframe.
/// </summary>
public sealed record PromptTemplateKey(PromptLayer Layer, AiRole? Role = null, Market? Market = null, Timeframe? Timeframe = null)
{
    public PromptLayer Layer { get; init; } = Check(Layer, Role, Market, Timeframe);

    /// <summary>
    /// Ranks the keys that match one combination. Two different keys that match the same combination differ in
    /// which dimensions they set, so they never rank the same.
    /// </summary>
    public int Specificity =>
        ((Count(Role) + Count(Market) + Count(Timeframe)) << 3) | (Count(Role) << 2) | (Count(Market) << 1) | Count(Timeframe);

    /// <summary>Whether this key's template applies to the combination: every dimension it sets is the combination's.</summary>
    public bool Matches(AiRole role, Market market, Timeframe timeframe) =>
        (Role is null || Role == role) && (Market is null || Market == market) && (Timeframe is null || Timeframe == timeframe);

    public override string ToString()
    {
        var dimensions = new[] { Role?.ToString(), Market?.ToString(), Timeframe?.ToString() }.Where(value => value is not null);

        return string.Create(CultureInfo.InvariantCulture, $"{Layer} ({string.Join(", ", dimensions)})");
    }

    private static int Count<T>(T? value)
        where T : struct => value is null ? 0 : 1;

    private static PromptLayer Check(PromptLayer layer, AiRole? role, Market? market, Timeframe? timeframe)
    {
        if (!Enum.IsDefined(layer))
        {
            throw new ArgumentOutOfRangeException(nameof(Layer), layer, "Not a prompt layer.");
        }

        if ((role is { } r && !Enum.IsDefined(r)) || (market is { } m && !Enum.IsDefined(m)) || (timeframe is { } t && !Enum.IsDefined(t)))
        {
            throw new ArgumentOutOfRangeException(nameof(Layer), "A dimension isn't a role, market or timeframe.");
        }

        var missing = layer switch
        {
            PromptLayer.Role or PromptLayer.OutputContract when role is null => "a role",
            PromptLayer.Market when market is null => "a market",
            PromptLayer.Timeframe when timeframe is null => "a timeframe",
            _ => null,
        };

        return missing is null
            ? layer
            : throw new ArgumentException($"A {layer} template needs {missing}.", nameof(Layer));
    }
}

/// <summary>
/// One version of a template. A version never changes: an edit is a new version (FR-PRM-03), and the highest
/// version of a key is the one in use.
/// </summary>
/// <param name="Id">The version's row, which an AI call records (FR-PRM-03).</param>
/// <param name="Content">The layer's text, which goes into the prompt as it is.</param>
public sealed record PromptTemplate(Guid Id, PromptTemplateKey Key, int Version, string Content)
{
    public PromptTemplateKey Key { get; init; } = Key ?? throw new ArgumentNullException(nameof(Key));

    public int Version { get; init; } = Version >= 1
        ? Version
        : throw new ArgumentOutOfRangeException(nameof(Version), Version, "Versions start at 1.");

    public string Content { get; init; } = Content switch
    {
        null => throw new ArgumentNullException(nameof(Content)),
        _ when string.IsNullOrWhiteSpace(Content) => throw new ArgumentException("A template needs some text.", nameof(Content)),
        { Length: > PromptLimits.TemplateMaxLength } =>
            throw new ArgumentException($"A template is at most {PromptLimits.TemplateMaxLength} characters.", nameof(Content)),
        _ => Content,
    };

    public PromptTemplateVersion ToVersion() => new(Id, Key, Version);
}

/// <summary>Which version of a template a prompt used, which every AI call records (FR-PRM-03).</summary>
public sealed record PromptTemplateVersion(Guid Id, PromptTemplateKey Key, int Version);

public static class PromptLimits
{
    /// <summary>FR-PRM-05. The same as Agents' limit, which a test checks, and enforced again here.</summary>
    public const int CustomInstructionsMaxLength = 2000;

    /// <summary>The longest template.</summary>
    public const int TemplateMaxLength = 16_000;

    /// <summary>
    /// The most of one untrusted item that goes into a prompt (FR-PRM-06). Beyond it, the text is cut and marked,
    /// so one long article can't crowd out the rest, or run up the user's bill.
    /// </summary>
    public const int UntrustedItemMaxLength = 4000;
}

/// <summary>No template matches one of the layers for a combination, so the prompt would be incomplete (FR-PRM-02).</summary>
public sealed class PromptTemplateMissingException(PromptLayer layer, AiRole role, Market market, Timeframe timeframe)
    : Exception($"No {layer} template matches {role}, {market}, {timeframe}.")
{
    public PromptLayer Layer { get; } = layer;

    public AiRole Role { get; } = role;

    public Market Market { get; } = market;

    public Timeframe Timeframe { get; } = timeframe;
}
