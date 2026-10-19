using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Configuration;

/// <summary>Validates the fair-use limits of the <c>Agents</c> section at startup (FR-CFG-06, FR-ACC-07).</summary>
public sealed class AgentLimitsOptionsValidator : IValidateOptions<AgentLimitsOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentLimitsOptions options)
    {
        var failures = new List<string>();
        var prefix = AgentLimitsOptions.SectionName;

        if (options.MaxAgentsPerUser <= 0)
            failures.Add($"{prefix}:MaxAgentsPerUser must be > 0.");
        if (options.MaxRunningAgentsPerUser <= 0)
            failures.Add($"{prefix}:MaxRunningAgentsPerUser must be > 0.");
        if (options.MaxRunningAgentsPerUser > options.MaxAgentsPerUser)
            failures.Add($"{prefix}:MaxRunningAgentsPerUser must be <= {prefix}:MaxAgentsPerUser.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
