using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// At registration the user accepts the Terms of Service, the Privacy Policy, and the risk
/// disclosure. The accepted versions and the time are recorded (FR-ACC-8).
/// </summary>
public class TermsAcceptanceTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LegalDocument.TermsOfService, RegistrationProblem.TermsOfServiceNotAccepted)]
    [InlineData(LegalDocument.PrivacyPolicy, RegistrationProblem.PrivacyPolicyNotAccepted)]
    [InlineData(LegalDocument.RiskDisclosure, RegistrationProblem.RiskDisclosureNotAccepted)]
    public async Task Registration_cannot_complete_without_accepting_each_document(
        LegalDocument left, RegistrationProblem problem)
    {
        using var application = new AccountsApplication();

        var result = await application.RegisterAsync(
            Ada, accepted: [.. AccountsApplication.EveryDocument.Where(accepted => accepted.Document != left)]);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal([problem], result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task Accepting_nothing_names_every_document()
    {
        using var application = new AccountsApplication();

        var result = await application.RegisterAsync(Ada, accepted: []);

        Assert.Equal(
            [
                RegistrationProblem.TermsOfServiceNotAccepted,
                RegistrationProblem.PrivacyPolicyNotAccepted,
                RegistrationProblem.RiskDisclosureNotAccepted,
            ],
            result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
    }

    [Fact]
    public async Task A_version_that_is_not_in_force_is_not_accepted()
    {
        using var application = new AccountsApplication();

        // What a form posts that was opened before the Terms of Service changed.
        var result = await application.RegisterAsync(
            Ada,
            accepted:
            [
                .. AccountsApplication.EveryDocument.Where(accepted => accepted.Document != LegalDocument.TermsOfService),
                new AcceptedDocument(LegalDocument.TermsOfService, "0.0"),
            ]);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal([RegistrationProblem.TermsOfServiceNotAccepted], result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));
    }

    [Fact]
    public async Task The_accepted_versions_and_the_time_are_recorded()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));

        await application.RegisterAsync(Ada);

        var user = StoredUser.Of(application.Database, Ada)!;
        Assert.Equal(
            [
                new StoredAcceptance(user.Id, "PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy), Morning),
                new StoredAcceptance(user.Id, "RiskDisclosure", LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure), Morning),
                new StoredAcceptance(user.Id, "TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService), Morning),
            ],
            StoredAcceptance.All(application.Database));
    }

    [Fact]
    public void Every_document_has_a_version_in_force()
    {
        Assert.Equal(
            [LegalDocument.TermsOfService, LegalDocument.PrivacyPolicy, LegalDocument.RiskDisclosure],
            LegalDocuments.All);

        Assert.All(LegalDocuments.All, document => Assert.False(string.IsNullOrWhiteSpace(LegalDocuments.CurrentVersionOf(document))));
    }

    [Fact]
    public async Task The_records_go_with_the_account_when_it_is_deleted()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync(Ada);
        await application.RegisterAsync("grace@example.com");

        application.Database.Execute($"DELETE FROM users WHERE email = '{Ada}'");

        var grace = StoredUser.Of(application.Database, "grace@example.com")!;
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);
        Assert.All(StoredAcceptance.All(application.Database), acceptance => Assert.Equal(grace.Id, acceptance.UserId));
    }
}
