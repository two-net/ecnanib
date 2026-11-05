using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// The models a user can give each role, and the assignment a new agent starts with (FR-ROLE-02, FR-ROLE-03).
/// </summary>
/// <param name="Options">
/// For every role, the models that are Active, that one of the user's valid AI keys can use, and that don't lack
/// structured output. The Sentiment Analyst's models with live search come first, then each list is by
/// provider and name.
/// </param>
/// <param name="Defaults">
/// A model and, where the user has another, a fallback for each role the user's models can fill. Empty when
/// they can't fill any.
/// </param>
public sealed record RoleChoices(IReadOnlyDictionary<AiRole, IReadOnlyList<ModelChoice>> Options, IReadOnlyList<RoleAssignment> Defaults);

/// <param name="DisplayName">The catalog's name for it.</param>
/// <param name="LiveSearch">Whether it can use its provider's web or X search, or null when nobody said.</param>
public sealed record ModelChoice(ModelRef Model, string DisplayName, bool? LiveSearch);
