using System.Globalization;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// The decision cycle's setting in the Agents section. Read() is the only way in. A missing value takes its default,
/// and an invalid one stops startup with a message that names it (CFG-03).
/// </summary>
internal sealed class DecisionCycleSettings
{
    public const string CycleErrorsBeforePauseKey = "Agents:CycleErrorsBeforePause";

    public const int DefaultCycleErrorsBeforePause = 3;

    public const int MaximumCycleErrorsBeforePause = 100;

    private DecisionCycleSettings(int cycleErrorsBeforePause) => CycleErrorsBeforePause = cycleErrorsBeforePause;

    /// <summary>
    /// How many cycles in a row may end without a decision before the agent goes to Error. Zero means never.
    /// </summary>
    public int CycleErrorsBeforePause { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static DecisionCycleSettings Read(IConfiguration configuration)
    {
        var value = configuration[CycleErrorsBeforePauseKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return new DecisionCycleSettings(DefaultCycleErrorsBeforePause);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{CycleErrorsBeforePauseKey} must be a whole number.");
        }

        if (number is < 0 or > MaximumCycleErrorsBeforePause)
        {
            throw new InvalidOperationException($"{CycleErrorsBeforePauseKey} must be between 0 and {MaximumCycleErrorsBeforePause}.");
        }

        return new DecisionCycleSettings(number);
    }
}
