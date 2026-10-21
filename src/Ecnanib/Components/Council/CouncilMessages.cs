using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Components.Council;

/// <summary>User-facing wording of a deliberation (FR-AI-14): role statuses, actions and limit outcomes.</summary>
internal static class CouncilMessages
{
    public static string Status(RoleOutputStatus status) => status switch
    {
        RoleOutputStatus.Succeeded => "succeeded",
        RoleOutputStatus.Failed => "failed",
        RoleOutputStatus.TimedOut => "timed out",
        RoleOutputStatus.Disabled => "disabled",
        RoleOutputStatus.Skipped => "skipped",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string StatusClass(RoleOutputStatus status) => "status-" + status.ToString().ToLowerInvariant();

    public static string Action(DecisionAction? action) => action switch
    {
        DecisionAction.OpenLong => "Open long",
        DecisionAction.OpenShort => "Open short",
        DecisionAction.Hold => "Hold",
        DecisionAction.Modify => "Modify",
        DecisionAction.Close => "Close",
        DecisionAction.NoTrade => "No trade",
        null => "none",
        _ => action.ToString()!,
    };

    public static string Limit(LimitCheckStatus status) => status switch
    {
        LimitCheckStatus.Pass => "pass",
        LimitCheckStatus.Clamped => "clamped",
        LimitCheckStatus.Fail => "fail",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string LimitClass(LimitCheckStatus status) => "limit-" + status.ToString().ToLowerInvariant();
}
