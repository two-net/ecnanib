namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// Fair-use limits from the configuration file, identical for all users (FR-ACC-07). The agents module calls these at
/// creation and start time (FR-AGT-15). Running means Running or Paused; an agent in Error is not counted.
/// </summary>
public interface IFairUseLimits
{
    Task<FairUseCheck> CheckCreateAgentAsync(Guid userId, CancellationToken cancellationToken);

    Task<FairUseCheck> CheckStartAgentAsync(Guid userId, CancellationToken cancellationToken);
}
