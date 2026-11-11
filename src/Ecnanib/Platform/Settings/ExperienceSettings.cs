namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Experience</c> section: lessons, strength scores, and the platform-wide board.</summary>
/// <param name="LessonsPerPrompt">The most recent lessons a prompt may include (FR-EXP-7).</param>
/// <param name="MinSamples">The sample size below which a slice shows "not enough data" (FR-EXP-4).</param>
/// <param name="Damping">The constant that damps a strength score toward zero for small samples (FR-EXP-4).</param>
/// <param name="Board">When a slice may appear on the platform-wide board.</param>
public sealed record ExperienceSettings(
    int LessonsPerPrompt,
    int MinSamples,
    int Damping,
    ExperienceBoardSettings Board)
{
    internal static ExperienceSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Experience:LessonsPerPrompt", minimum: 0),
            file.WholeNumber("Experience:MinSamples"),
            file.WholeNumber("Experience:Damping", minimum: 0),
            new ExperienceBoardSettings(
                file.WholeNumber("Experience:Board:MinUsers"),
                file.WholeNumber("Experience:Board:MinTrades")));
}

/// <summary>The fewest users and trades that must contribute to a slice of the board (FR-EXP-10).</summary>
public sealed record ExperienceBoardSettings(int MinUsers, int MinTrades);
