using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Architecture;

/// <summary>
/// What the model and the UI must never name. The only account type is the individual user, so
/// there is no tenant, organization, workspace, team, or shared resource (FR-ACC-5). The service
/// is free, so there is no plan, subscription, payment, invoice, trial, or paid tier (FR-ACC-6).
/// This file is the source of truth: <c>docs/ownership.md</c> explains the rule, and
/// <see cref="IndividualServiceTests"/> enforces it.
/// </summary>
/// <remarks>
/// A name is its words: <c>team_members</c> is "team" and "members", and <c>explanations</c> is
/// not "plan". The rule cannot tell what a name means, so a word that is used in another sense,
/// such as the team of AI roles or a trade plan, is allowed by name in <see cref="Accepted"/>.
/// </remarks>
internal static partial class IndividualServiceRules
{
    /// <summary>The words, each with what it would bring in.</summary>
    private static readonly (string Word, string Brings)[] Forbidden =
    [
        ("tenant", "a tenant"),
        ("organization", "an organization"),
        ("organisation", "an organization"),
        ("workspace", "a workspace"),
        ("team", "a team"),
        ("member", "a team"),
        ("share", "a shared resource"),
        ("shared", "a shared resource"),

        ("plan", "a plan"),
        ("subscription", "a subscription"),
        ("payment", "a payment"),
        ("invoice", "an invoice"),
        ("billing", "billing"),
        ("trial", "a trial"),
        ("tier", "a paid tier"),
        ("paid", "a paid tier"),
    ];

    /// <summary>
    /// Names that hold one of the words and mean something else, each with what it means. To add
    /// one is to decide, here where it shows, that the name is about neither tenants nor money.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Accepted = new Dictionary<string, string>();

    /// <summary>The names among <paramref name="names"/> that hold a forbidden word, with what it would bring in.</summary>
    public static IReadOnlyList<string> FindIn(IEnumerable<string> names) => FindIn(names, Accepted);

    /// <summary>The names that hold a forbidden word and are not accepted.</summary>
    public static IReadOnlyList<string> FindIn(IEnumerable<string> names, IReadOnlyDictionary<string, string> accepted)
    {
        var found =
            from name in names.Distinct()
            where !accepted.ContainsKey(name)
            let words = WordsOf(name)
            let brings = Forbidden.Where(forbidden => words.Contains(forbidden.Word)).Select(forbidden => forbidden.Brings).Distinct().ToList()
            where brings.Count > 0
            select $"{name}: names {string.Join(" and ", brings)}";

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>The names of a model: each table, and each column as <c>table.column</c>.</summary>
    public static IReadOnlyList<string> NamesOf(IModel model) =>
    [
        .. from entity in model.GetEntityTypes()
           let table = entity.GetTableName() ?? entity.ClrType.Name
           from name in entity.GetProperties().Select(property => $"{table}.{property.GetColumnName()}").Prepend(table)
           select name,
    ];

    /// <summary>
    /// The words of a name, in lower case, each also without the "s" of its plural:
    /// <c>/billing/Invoices/{id}</c> is billing, invoices, invoice, and id.
    /// </summary>
    private static HashSet<string> WordsOf(string name)
    {
        var words = new HashSet<string>();

        foreach (var word in Word().Matches(name).Select(match => match.Value.ToLowerInvariant()))
        {
            words.Add(word);

            if (word.Length > 3 && word.EndsWith('s'))
            {
                words.Add(word[..^1]);
            }
        }

        return words;
    }

    // A run of small letters with the capital before it, or a run of capitals: the words of
    // snake_case, kebab-case, PascalCase, and a path alike.
    [GeneratedRegex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])")]
    private static partial Regex Word();
}
