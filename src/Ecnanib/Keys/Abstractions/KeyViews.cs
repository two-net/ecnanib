using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Keys.Abstractions;

// Everything the pages show. No view carries a key or secret value; the key appears masked only (FR-KEY-04, FR-UI-06).

/// <summary><see cref="ActiveAgents"/> counts the Running or Paused live agents that use the key; they block its deletion (FR-KEY-06).</summary>
public sealed record ExchangeKeyView(
    Guid Id,
    string Label,
    ExchangeEnvironment Environment,
    bool AllowSpot,
    bool AllowFutures,
    string MaskedApiKey,
    KeyStatus Status,
    DateTimeOffset? LastValidatedAt,
    string? LastValidationError,
    ApiKeyPermissions? Permissions,
    int ActiveAgents,
    DateTimeOffset CreatedAt);

public sealed record ProviderKeyView(
    Guid Id,
    AiProvider Provider,
    string MaskedApiKey,
    KeyStatus Status,
    DateTimeOffset? LastValidatedAt,
    string? LastValidationError,
    DateTimeOffset CreatedAt);

/// <summary>
/// One entry per provider. <see cref="Key"/> is null when the user has none, which the UI says plainly (FR-KEY-08).
/// <see cref="ActiveAgents"/> counts the Running or Paused agents with an enabled council role on the provider.
/// </summary>
public sealed record ProviderKeySlot(AiProvider Provider, ProviderKeyView? Key, int ActiveAgents);

public sealed record KeyOverview(IReadOnlyList<ExchangeKeyView> ExchangeKeys, IReadOnlyList<ProviderKeySlot> Providers);
