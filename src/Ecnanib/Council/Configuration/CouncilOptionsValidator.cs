using Ecnanib.Agents.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Council.Configuration;

/// <summary>
/// Validates the <c>Council</c> section at startup so an invalid file refuses to start (FR-CFG-06). The execution
/// reserve is also checked against the agents module's shortest evaluation deadline (NFR-PERF-01): the Risk Manager
/// must fit between the two, or no council could ever decide.
/// </summary>
public sealed class CouncilOptionsValidator : IValidateOptions<CouncilOptions>
{
    private readonly IOptions<AgentsOptions> _agents;

    public CouncilOptionsValidator(IOptions<AgentsOptions> agents)
    {
        _agents = agents;
    }

    public ValidateOptionsResult Validate(string? name, CouncilOptions options)
    {
        var failures = new List<string>();
        var prefix = CouncilOptions.SectionName;

        if (options.MinimumRoleBudgetSeconds <= 0)
            failures.Add($"{prefix}:MinimumRoleBudgetSeconds must be > 0.");
        if (options.RoleTimeoutSeconds <= 0)
            failures.Add($"{prefix}:RoleTimeoutSeconds must be > 0.");
        else if (options.RoleTimeoutSeconds < options.MinimumRoleBudgetSeconds)
            failures.Add($"{prefix}:RoleTimeoutSeconds must be at least the minimum role budget.");
        if (options.RiskManagerReserveSeconds < 0)
            failures.Add($"{prefix}:RiskManagerReserveSeconds must be >= 0.");
        else if (options.RiskManagerReserveSeconds < options.MinimumRoleBudgetSeconds)
            failures.Add($"{prefix}:RiskManagerReserveSeconds must be at least the minimum role budget.");
        if (options.ExecutionReserveSeconds <= 0)
            failures.Add($"{prefix}:ExecutionReserveSeconds must be > 0.");
        else if (options.MinimumRoleBudgetSeconds > 0 && ShortestDeadline() is { } shortest && shortest > TimeSpan.Zero
                 && TimeSpan.FromSeconds(options.ExecutionReserveSeconds + options.MinimumRoleBudgetSeconds) >= shortest)
            failures.Add($"{prefix}:ExecutionReserveSeconds plus {prefix}:MinimumRoleBudgetSeconds must leave the Risk Manager time within the shortest Agents:EvaluationDeadlineSeconds ({shortest.TotalSeconds:0} s).");
        if (options.RecentCandles <= 0)
            failures.Add($"{prefix}:RecentCandles must be > 0.");
        if (options.HigherTimeframeCandles < 0)
            failures.Add($"{prefix}:HigherTimeframeCandles must be >= 0.");
        if (options.MaxOutputTokens <= 0)
            failures.Add($"{prefix}:MaxOutputTokens must be > 0.");
        if (options.MaxAssessmentLength <= 0)
            failures.Add($"{prefix}:MaxAssessmentLength must be > 0.");
        if (options.MaxRationaleLength is <= 0 or > 3000)
            failures.Add($"{prefix}:MaxRationaleLength must be between 1 and 3000.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private TimeSpan? ShortestDeadline()
    {
        var agents = _agents.Value;
        return AgentsOptions.Timeframes.Min(t => agents.DeadlineFor(t));
    }
}
