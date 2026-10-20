using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Strategies.Engine;

/// <summary>One Appendix A strategy at one version: its catalog descriptor and its deterministic evaluation.</summary>
internal interface IStrategy
{
    StrategyDescriptor Descriptor { get; }

    StrategyOutput Evaluate(StrategyContext context);
}
