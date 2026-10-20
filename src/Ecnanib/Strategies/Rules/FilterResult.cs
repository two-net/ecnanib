namespace Ecnanib.Strategies.Rules;

internal enum FilterVerdict
{
    Passed,
    Blocked,
    Informational,
}

/// <summary>One applied filter (FR-STR-02): a blocked one suppresses the setup; an informational one annotates it (and may carry a size multiplier).</summary>
internal sealed record FilterResult(string Name, FilterVerdict Verdict, string Detail, decimal? SizeMultiplier = null)
{
    public bool Blocks => Verdict == FilterVerdict.Blocked;

    public static FilterResult Passed(string name, string detail) => new(name, FilterVerdict.Passed, detail);

    public static FilterResult Blocked(string name, string detail) => new(name, FilterVerdict.Blocked, detail);

    public static FilterResult Info(string name, string detail, decimal? sizeMultiplier = null) => new(name, FilterVerdict.Informational, detail, sizeMultiplier);

    public override string ToString() => $"{Name}: {Detail} ({Verdict switch
    {
        FilterVerdict.Passed => "passed",
        FilterVerdict.Blocked => "blocked",
        _ => "info",
    }})";
}
