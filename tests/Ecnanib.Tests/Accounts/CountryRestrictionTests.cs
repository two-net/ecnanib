using Ecnanib.Accounts;
using Ecnanib.Shared;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi.Pages.Account;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Restrictions by country are applied (NFR-LEG-3): the configuration file names the countries
/// that the service is not offered in, and nobody who says that they live in one of them is
/// registered. While the file names none, no visitor is asked where they live. What a visitor
/// answers is held against the file and is not kept. See docs/legal.md.
/// </summary>
public class CountryRestrictionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // Antarctica and Bouvet Island, where nobody registers from, are the two that the service
    // of these tests is not offered in. Iceland is one that it is offered in.
    private const string Antarctica = "AQ";
    private const string BouvetIsland = "BV";
    private const string Iceland = AccountsApplication.Country;

    private static AccountsApplication Restricting(Func<SettingsJson, SettingsJson>? further = null) =>
        new(settings: file =>
        {
            file.WithJson("Application:RestrictedCountries", $"[\"{Antarctica}\", \"{BouvetIsland}\"]");

            return further?.Invoke(file) ?? file;
        });

    [Fact]
    public async Task While_the_file_names_no_country_nobody_is_asked_where_they_live()
    {
        using var application = new AccountsApplication();

        Assert.False(application.Registration.AsksCountry);

        // A registration is not held to a country then, whatever it brings: none, one that
        // another file restricts, or something that is no country.
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada, country: null)).Outcome);
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Grace, country: Antarctica)).Outcome);
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync("alan@example.com", country: "nowhere")).Outcome);

        Assert.Equal(3, StoredUser.All(application.Database).Count);
    }

    [Fact]
    public async Task A_visitor_who_lives_where_the_service_is_offered_registers_as_before()
    {
        using var application = Restricting();

        Assert.True(application.Registration.AsksCountry);

        var result = await application.RegisterAsync(Ada, country: Iceland);

        Assert.Equal(RegistrationOutcome.Accepted, result.Outcome);
        Assert.Empty(result.Problems);
        Assert.Equal(Ada, Assert.Single(StoredUser.All(application.Database)).Email);
        Assert.Equal(3, StoredAcceptance.All(application.Database).Count);
        Assert.Equal(Ada, Assert.Single(application.Email.Sent).To);
    }

    [Theory]
    [InlineData(Antarctica)]
    [InlineData(BouvetIsland)]
    public async Task A_visitor_who_lives_in_a_country_that_the_file_names_is_not_registered(string country)
    {
        using var application = Restricting();

        var result = await application.RegisterAsync(Ada, country: country);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal([RegistrationProblem.CountryRestricted], result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(StoredAcceptance.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("is")]
    [InlineData(" IS")]
    [InlineData("ISL")]
    [InlineData("Iceland")]

    // No code: the United Kingdom is GB.
    [InlineData("UK")]
    public async Task A_registration_that_is_asked_for_a_country_and_names_none_is_refused(string? country)
    {
        using var application = Restricting();

        var result = await application.RegisterAsync(Ada, country: country);

        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal([RegistrationProblem.CountryNotChosen], result.Problems);
        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Fact]
    public async Task The_country_is_refused_with_whatever_else_is_wrong_in_the_order_of_the_form()
    {
        using var application = Restricting();

        var result = await application.RegisterAsync("nobody", "short", accepted: [], country: Antarctica);

        Assert.Equal(
            [
                RegistrationProblem.EmailInvalid,
                RegistrationProblem.PasswordTooShort,
                RegistrationProblem.CountryRestricted,
                RegistrationProblem.TermsOfServiceNotAccepted,
                RegistrationProblem.PrivacyPolicyNotAccepted,
                RegistrationProblem.RiskDisclosureNotAccepted,
            ],
            result.Problems);
    }

    [Fact]
    public async Task A_registration_that_is_refused_for_its_country_spends_no_attempt()
    {
        // One registration for an address in the time that the limit counts over.
        using var application = Restricting(file => file.With("Limits:Registration:PerAddress", 1));

        // Refused before anything is counted, hashed, or sent, as a form that is filled in wrong is.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(
                [RegistrationProblem.CountryRestricted],
                (await application.RegisterAsync(Ada, country: Antarctica)).Problems);
        }

        // So the one registration that the limit allows is still there, and the next is not.
        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada, country: Iceland)).Outcome);
        Assert.Equal(RegistrationOutcome.TooManyAttempts, (await application.RegisterAsync(Ada, country: Iceland)).Outcome);
    }

    [Fact]
    public async Task What_a_visitor_answers_is_not_kept()
    {
        using var application = Restricting();

        Assert.Equal(RegistrationOutcome.Accepted, (await application.RegisterAsync(Ada, country: Iceland)).Outcome);
        Assert.Equal(RegistrationOutcome.Invalid, (await application.RegisterAsync(Grace, country: Antarctica)).Outcome);
        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[^1])));

        // Neither the database nor the log holds the country of the one who registered or of
        // the one who was refused: not its name, and not its code as a value of its own. Two
        // letters are among the letters of a hash by chance, so the code is looked for between
        // the quotes that every stored text and every property of an event stands in.
        var stored = application.EverythingStored();
        var log = application.LogText();

        Assert.Contains(Ada, stored);
        Assert.NotEmpty(log);

        foreach (var country in new[] { Iceland, Antarctica })
        {
            var name = CountryNames.NameOf(country)!;

            Assert.DoesNotContain(name, stored, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"\"{country}\"", stored, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{country}\"", log, StringComparison.Ordinal);
        }

        // Nor does the message that was sent to the address say it.
        Assert.All(application.Email.Sent, message => Assert.DoesNotContain(CountryNames.NameOf(Iceland)!, message.TextBody));

        // And the audit trail records no registration, with or without a country.
        Assert.Empty(StoredAuditEvent.All(application.Database));
    }

    [Fact]
    public void Every_country_has_a_code_and_one_name_and_the_file_names_a_country_by_its_code()
    {
        // The codes of ISO 3166-1, which are 249, and Kosovo.
        Assert.Equal(250, Countries.Codes.Count);
        Assert.Equal(Countries.Codes.Order(StringComparer.Ordinal), Countries.Codes);
        Assert.Equal(Countries.Codes.Count, Countries.Codes.Distinct().Count());
        Assert.All(Countries.Codes, code => Assert.Matches("^[A-Z]{2}$", code));
        Assert.Contains("XK", Countries.Codes);

        Assert.True(Countries.IsCode("GB"));
        Assert.All(new[] { null, "", "gb", "UK", "GBR", "G", " GB", "GB " }, code => Assert.False(Countries.IsCode(code)));

        // Each has a name, no name is of two, and no name is of a code that is none.
        Assert.Equal(Countries.Codes, CountryNames.InOrder.Select(country => country.Code).Order(StringComparer.Ordinal));
        Assert.Equal(CountryNames.InOrder.Count, CountryNames.InOrder.Select(country => country.Name).Distinct().Count());
        Assert.All(CountryNames.InOrder, country => Assert.False(string.IsNullOrWhiteSpace(country.Name)));
        Assert.All(CountryNames.InOrder, country => Assert.Equal(country.Name, CountryNames.NameOf(country.Code)));
        Assert.Null(CountryNames.NameOf("UK"));
        Assert.Null(CountryNames.NameOf(null));

        Assert.Equal("United Kingdom", CountryNames.NameOf("GB"));
        Assert.Equal("Kosovo", CountryNames.NameOf("XK"));
    }
}
