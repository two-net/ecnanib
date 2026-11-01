namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Names of the ambient properties in FR-LOG-04. Set them with <c>ILogger.BeginScope</c> and a
/// dictionary. Scopes flow with the async call, so code further down inherits them, including the
/// adapters, which can't reference this type.
/// </summary>
public static class LogProperties
{
    public const string CorrelationId = nameof(CorrelationId);

    public const string UserId = nameof(UserId);

    public const string AgentId = nameof(AgentId);

    public const string CycleId = nameof(CycleId);

    /// <summary>The agent's trading environment, Demo or Live.</summary>
    public const string Environment = nameof(Environment);
}
