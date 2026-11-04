using System.Reflection;
using Ecnanib.Components.Account;
using Ecnanib.Components.Credentials;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Tests.Credentials;

/// <summary>An issue that adds a status, a problem or a summary field has to give it text, or these fail.</summary>
public sealed class CredentialTextTests
{
    [Fact]
    public void Every_status_and_problem_has_text_of_its_own()
    {
        var statuses = Enum.GetValues<CredentialStatus>().Select(CredentialText.StatusOf).ToList();
        var problems = Enum.GetValues<CredentialProblem>().Select(CredentialText.ProblemOf).ToList();

        Assert.All(statuses.Concat(problems), text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(statuses.Count, statuses.Distinct().Count());
        Assert.Equal(problems.Count, problems.Distinct().Count());
    }

    [Fact]
    public void Every_outcome_of_a_users_sync_has_text_of_its_own()
    {
        var outcomes = Enum.GetValues<UserSyncOutcome>().Select(outcome => CredentialText.SyncResultOf(new UserSyncResult(outcome))).ToList();
        var partly = CredentialText.SyncResultOf(new UserSyncResult(UserSyncOutcome.Synced, Keys: 3, KeysFailed: 1, Models: 12));

        Assert.All(outcomes, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(outcomes.Count, outcomes.Distinct().Count());
        Assert.Equal(
            "Models synced: 12 available to your keys. The provider didn't answer for 1 of them, so those keep the models they had.",
            partly);
    }

    [Fact]
    public void Every_field_in_a_credential_s_summary_has_text()
    {
        var fields = typeof(CredentialAuditFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(ActivityText.FieldOf(field))));
    }

    [Theory]
    [InlineData(CredentialAuditFields.Status, "Valid", "Invalid", "from Valid to Invalid")]
    [InlineData(CredentialAuditFields.Status, "Valid", "Valid", "Valid")]
    [InlineData(CredentialAuditFields.Status, "InsufficientPermissions", null, "Insufficient permissions")]
    [InlineData(CredentialAuditFields.Environment, null, "Live", "Live")]
    [InlineData(CredentialAuditFields.Ending, "abcd", "wxyz", "from …abcd to …wxyz")]
    [InlineData(CredentialAuditFields.Label, null, "<b>Work</b>", "<b>Work</b>")]
    [InlineData(CredentialAuditFields.Status, "2", null, "2")]
    [InlineData("Unknown", "a", "b", null)]
    public void A_change_reads_as_a_line_of_text(string field, string? before, string? after, string? expected) =>
        Assert.Equal(expected, ActivityText.Describe(new ActivityChange(field, before, after)));
}
