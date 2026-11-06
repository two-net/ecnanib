using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// A decision cycle (FR-CYC-02). For now it only checks that the agent is still Active, because a pause stops new
/// cycles (FR-AGT-06), and the scheduler listed the agent a little earlier. The council, the Risk Engine and the
/// trace come with #31.
/// </summary>
internal sealed class DecisionCycles(IAgents agents, ILogger<DecisionCycles> logger) : IDecisionCycles
{
    public async Task RunAsync(CycleRequest request, JobContext context, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(request.UserId, request.AgentId, cancellationToken);

        if (agent?.Status != AgentStatus.Active)
        {
            logger.LogInformation(
                "Skipped the decision cycle for the candle that closed at {CandleClose}, because the agent is no longer Active",
                request.CandleClose);

            return;
        }

        logger.LogInformation(
            "Ran the decision cycle for the candle that closed at {CandleClose}, with a deadline of {Deadline}",
            request.CandleClose,
            request.Deadline);
    }
}
