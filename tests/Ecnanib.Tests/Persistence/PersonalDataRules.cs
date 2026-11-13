using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Persistence;

/// <summary>The kinds of personal data that may be stored (NFR-SEC-9).</summary>
internal enum PersonalDataKind
{
    EmailAddress,
    PasswordHash,
    Preference,
}

/// <summary>
/// What is stored about a person, in one place. Personal data is limited to the email address,
/// the password hash, and preferences (NFR-SEC-9). This file is the source of truth:
/// <c>docs/accounts.md</c> explains the rule, and <see cref="PersonalDataTests"/> enforces it.
/// </summary>
/// <remarks>
/// The rule cannot tell what a column holds. So it asks for every column of <c>users</c> and of
/// the tables of users' rows to be listed here with what it holds, unless its type cannot say
/// who someone is: an id, a flag, a number, a time, or one of a fixed set of names. To list a
/// column is to decide, here where it shows, what the application keeps about a person.
/// </remarks>
internal static class PersonalDataRules
{
    /// <summary>The columns that hold personal data, each with the kind that NFR-SEC-9 allows.</summary>
    public static readonly IReadOnlyDictionary<string, PersonalDataKind> Personal = new Dictionary<string, PersonalDataKind>
    {
        ["users.email"] = PersonalDataKind.EmailAddress,
        ["users.password_hash"] = PersonalDataKind.PasswordHash,
    };

    /// <summary>
    /// The columns that hold personal data of a kind that is not on the list of NFR-SEC-9, each
    /// with why it is kept all the same. To add one is to decide to keep more about a person
    /// than the requirement names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BeyondTheList = new Dictionary<string, string>
    {
        ["audit_events.origin"] =
            "The IP address a security-relevant action came from, which the specification lists for an audit event. " +
            "An audit trail that cannot say where an action came from does not show a takeover. It is in the export, " +
            "and goes with the account.",
    };

    /// <summary>
    /// The columns whose type could hold anything, and that say nothing about the person, each
    /// with what it holds.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotPersonal = new Dictionary<string, string>
    {
        ["users.email_verification_token_hash"] = "The hash of a random token, which the application made.",
        ["users.password_reset_token_hash"] = "The hash of a random token, which the application made.",
        ["users.two_factor_secret"] = "A random key that the application made, encrypted.",
        ["users.two_factor_secret_key_id"] = "The id of the master key of the configuration file that encrypted that key.",
        ["sessions.token_hash"] = "The hash of a random token, which the application made.",
        ["pending_sign_ins.token_hash"] = "The hash of a random token, which the application made.",
        ["two_factor_recovery_codes.code_hash"] = "The hash of a random code, which the application made.",
        ["legal_acceptances.version"] = "The version of a document of the service.",
        ["audit_events.target"] = "A kind and an id: user:<id> for an account, ai-key:<id> and exchange-key:<id> for a stored key.",
        ["ai_credentials.provider"] = "The code of an AI provider, which is one of the rows of ai_providers.",
        ["ai_credentials.encrypted_value"] = "A key that an AI provider made for its user, encrypted.",
        ["ai_credentials.master_key_id"] = "The id of the master key of the configuration file that encrypted that key.",
        ["ai_credentials.masked_suffix"] = "The last four characters of that key, which is all of it that is shown.",
        ["exchange_credentials.encrypted_value"] = "An API key and a secret key that Binance made for its user, encrypted.",
        ["exchange_credentials.master_key_id"] = "The id of the master key of the configuration file that encrypted those keys.",
        ["exchange_credentials.masked_suffix"] = "The last four characters of that API key, which is all of the two that is shown.",
    };

    /// <summary>Every column that is listed, in any of the three lists.</summary>
    public static IReadOnlyList<string> Listed => [.. Personal.Keys, .. BeyondTheList.Keys, .. NotPersonal.Keys];

    /// <summary>The columns of the application's model that break the rule, each with what to do about it.</summary>
    public static IReadOnlyList<string> FindViolations(IModel model) => FindViolations(model, typeof(User), Listed);

    /// <summary>The columns of a model that break the rule.</summary>
    /// <param name="model">The model.</param>
    /// <param name="account">The entity that is the account.</param>
    /// <param name="listed">The columns that are listed with what they hold, each as <c>table.column</c>.</param>
    public static IReadOnlyList<string> FindViolations(IModel model, Type account, IReadOnlyCollection<string> listed)
    {
        var accountEntity = model.FindEntityType(account)
            ?? throw new InvalidOperationException($"The model has no entity {account.Name}.");

        // The account, and every table whose rows are a user's. A table of the service holds
        // nothing about a user.
        var columns = (
            from entity in model.GetEntityTypes()
            where entity == accountEntity || typeof(IUserOwned).IsAssignableFrom(entity.ClrType)
            let table = entity.GetTableName() ?? entity.ClrType.Name
            from property in entity.GetProperties()
            select (Name: $"{table}.{property.GetColumnName()}", CouldSay: CouldSayWhoSomeoneIs(property.ClrType)))
            .ToDictionary(column => column.Name, column => column.CouldSay);

        var violations = new List<string>();

        violations.AddRange(
            from column in columns
            where column.Value && !listed.Contains(column.Key)
            select $"{column.Key}: could say who a user is, and is not listed. List it in {nameof(PersonalDataRules)}: as " +
                "personal data of a kind that NFR-SEC-9 allows, or with what it holds instead.");

        violations.AddRange(
            from name in listed
            where !columns.ContainsKey(name)
            select $"{name}: is listed, but no table of users' rows has such a column. Take it off the list.");

        violations.AddRange(
            from name in listed
            where columns.TryGetValue(name, out var couldSay) && !couldSay
            select $"{name}: is listed, but its type cannot say who someone is. Take it off the list.");

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a value of a type could say who someone is. An id, a flag, a whole or exact
    /// number, a moment in time, and a member of an enum cannot. Everything else is taken to be
    /// able to: text, bytes, an address of the network, a document, a date of the calendar.
    /// </summary>
    private static bool CouldSayWhoSomeoneIs(Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;

        return !(value.IsEnum
            || value == typeof(Guid)
            || value == typeof(bool)
            || value == typeof(byte)
            || value == typeof(short)
            || value == typeof(int)
            || value == typeof(long)
            || value == typeof(decimal)
            || value == typeof(DateTimeOffset)
            || value == typeof(DateTime));
    }
}
