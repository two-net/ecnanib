using System.Globalization;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// What a rule looked at, in order (FR-RISK-10). Values are written in the invariant culture, so a trace reads
/// the same on every server, and the same inputs always give the same text.
/// </summary>
internal sealed class Inputs
{
    private readonly List<RuleInput> _values = [];

    public IReadOnlyList<RuleInput> Values => _values;

    public Inputs Add(string name, decimal? value) =>
        Add(name, value is { } number ? ExchangeMath.Normalize(number).ToString(CultureInfo.InvariantCulture) : None);

    public Inputs Add(string name, int? value) =>
        Add(name, value is { } number ? number.ToString(CultureInfo.InvariantCulture) : None);

    public Inputs Add(string name, bool value) => Add(name, value ? "true" : "false");

    public Inputs Add(string name, DateTimeOffset? value) =>
        Add(name, value is { } time ? time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : None);

    public Inputs Add<TEnum>(string name, TEnum value)
        where TEnum : struct, Enum =>
        Add(name, value.ToString());

    public Inputs Add<TEnum>(string name, TEnum? value)
        where TEnum : struct, Enum =>
        Add(name, value is { } member ? member.ToString() : None);

    public Inputs Add(string name, string value)
    {
        _values.Add(new RuleInput(name, value));

        return this;
    }

    private const string None = "none";
}

/// <summary>The results so far, so a rule can ask how the ones it depends on went.</summary>
internal sealed class Trace
{
    private readonly List<RuleResult> _results = [];

    public IReadOnlyList<RuleResult> Results => _results;

    public bool Failed => _results.Exists(result => result.Outcome == RuleOutcome.Fail);

    public void Add(RuleResult result) => _results.Add(result);

    /// <summary>
    /// Whether each rule passed. A rule that didn't apply counts too, because it set nothing up: a rule that
    /// depends on one that doesn't apply doesn't apply either.
    /// </summary>
    public bool Passed(params RiskRule[] rules) =>
        rules.All(rule => _results.Find(result => result.Rule == rule)?.Outcome is RuleOutcome.Pass or RuleOutcome.NotApplicable);

    public static RuleResult Pass(RiskRule rule, Inputs inputs) => new(rule, RuleOutcome.Pass, null, inputs.Values);

    public static RuleResult Fail(RiskRule rule, RiskReason reason, Inputs inputs) => new(rule, RuleOutcome.Fail, reason, inputs.Values);

    public static RuleResult NotApplicable(RiskRule rule) => new(rule, RuleOutcome.NotApplicable, null, []);

    public static RuleResult NotEvaluated(RiskRule rule) => new(rule, RuleOutcome.NotEvaluated, null, []);
}
