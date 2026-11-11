namespace Ecnanib.Platform.Settings;

/// <summary>
/// One thing wrong with the configuration file: the key and the rule it breaks. A problem with
/// the file as a whole has no key. A problem never carries a value from the file, so reporting it
/// cannot disclose a secret (NFR-CFG-4).
/// </summary>
public sealed record SettingsProblem(string? Key, string Rule)
{
    public override string ToString() => Key is null ? Rule : $"{Key}: {Rule}";
}

/// <summary>
/// The configuration file is missing or invalid, so the application must not start. The message
/// names the file and lists every problem found, not only the first.
/// </summary>
public sealed class InvalidSettingsException(string summary, IReadOnlyList<SettingsProblem> problems)
    : Exception(Describe(summary, problems))
{
    public IReadOnlyList<SettingsProblem> Problems { get; } = problems;

    private static string Describe(string summary, IReadOnlyList<SettingsProblem> problems) =>
        string.Join(Environment.NewLine, [summary, .. problems.Select(problem => $"  {problem}")]);
}
