namespace Ecnanib.Platform.Logging;

/// <summary>
/// The properties that tie log events to what they belong to: a request, a user, an agent, a
/// cycle, a role call, an exchange order (NFR-LOG-3). Their names are defined here and nowhere
/// else. Set the ones that are known and leave the rest null.
/// </summary>
/// <param name="RequestId">The HTTP request.</param>
/// <param name="UserId">The signed-in user.</param>
/// <param name="AgentId">The agent.</param>
/// <param name="CycleId">The decision cycle of an agent.</param>
/// <param name="Role">The role in the team that a model is called for.</param>
/// <param name="Provider">The AI provider, by its code.</param>
/// <param name="Model">The provider's identifier of the model.</param>
/// <param name="ExchangeOrderId">The exchange's identifier of an order.</param>
public sealed record Correlation(
    string? RequestId = null,
    string? UserId = null,
    string? AgentId = null,
    string? CycleId = null,
    string? Role = null,
    string? Provider = null,
    string? Model = null,
    string? ExchangeOrderId = null);

/// <summary>Opens the scopes that carry <see cref="Correlation"/> properties.</summary>
public static class CorrelationScope
{
    /// <summary>
    /// Adds the properties that are set in <paramref name="correlation"/> to every event written
    /// until the result is disposed, by this logger or any other, across awaits. Scopes nest: an
    /// inner scope adds to the outer one, and replaces a property that both set.
    /// </summary>
    public static IDisposable? BeginCorrelation(this ILogger logger, Correlation correlation)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(correlation);

        // A list of pairs is what the outputs turn into properties of the event.
        var properties = new List<KeyValuePair<string, object?>>();
        Add(nameof(Correlation.RequestId), correlation.RequestId);
        Add(nameof(Correlation.UserId), correlation.UserId);
        Add(nameof(Correlation.AgentId), correlation.AgentId);
        Add(nameof(Correlation.CycleId), correlation.CycleId);
        Add(nameof(Correlation.Role), correlation.Role);
        Add(nameof(Correlation.Provider), correlation.Provider);
        Add(nameof(Correlation.Model), correlation.Model);
        Add(nameof(Correlation.ExchangeOrderId), correlation.ExchangeOrderId);

        return logger.BeginScope(properties);

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                properties.Add(new KeyValuePair<string, object?>(name, value));
            }
        }
    }
}
