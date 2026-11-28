using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Account;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The form that registers a visitor asks which country they live in while the configuration
/// file names a country that the service is not offered in, and says so to a visitor who lives
/// in one (NFR-LEG-3). While the file names none, the form is as it was. See docs/legal.md.
/// </summary>
public partial class CountryRestrictionPageTests
{
    private const string Ada = "ada@example.com";

    // Antarctica is the one that the service of these tests is not offered in.
    private const string Antarctica = "AQ";
    private const string Iceland = AccountsApplication.Country;

    private static readonly (string Name, string Value)[] EveryDocumentAccepted =
    [
        ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
        ("PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
        ("RiskDisclosure", LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure)),
    ];

    private static AccountsApplication Restricting() =>
        new(settings: file => file.WithJson("Application:RestrictedCountries", $"[\"{Antarctica}\"]"));

    [Fact]
    public async Task While_the_file_names_no_country_the_form_asks_for_none_and_takes_no_notice_of_one()
    {
        using var application = new AccountsApplication();
        using var client = application.CreateClient();

        var page = await client.PageAsync("/register");

        Assert.DoesNotContain("name=\"Country\"", page);
        Assert.DoesNotContain("<select", page);
        Assert.DoesNotContain(RegistrationPages.CountryLabel, page);

        // A form that brings one all the same is registered, whichever it is.
        var (path, _) = await client.SubmitAsync(
            "/register",
            "register",
            [("Email", Ada), ("Password", AccountsApplication.Password), ("Country", Antarctica), .. EveryDocumentAccepted]);

        Assert.Equal("/register/sent", path);
        Assert.Equal(Ada, Assert.Single(StoredUser.All(application.Database)).Email);
    }

    [Fact]
    public async Task While_the_file_names_a_country_the_form_asks_which_country_the_visitor_lives_in()
    {
        using var application = Restricting();
        using var client = application.CreateClient();

        var page = Read(await client.PageAsync("/register"));
        var field = Field().Match(page).Value;

        // One field, between the password and the documents, with a label, and a hint that says
        // why it asks and that the answer is not kept.
        Assert.True(page.IndexOf("name=\"Password\"", StringComparison.Ordinal) < page.IndexOf(field, StringComparison.Ordinal));
        Assert.True(page.IndexOf(field, StringComparison.Ordinal) < page.IndexOf("name=\"TermsOfService\"", StringComparison.Ordinal));
        Assert.Contains("<label for=\"country\">Country you live in</label>", field);
        Assert.Contains("<select id=\"country\" name=\"Country\" autocomplete=\"country\" required aria-describedby=\"country-hint\">", field);
        Assert.Contains(
            "<p id=\"country-hint\" class=\"field-hint\">The service is not offered in every country. " +
            "Your answer is checked when you register, and is not kept.</p>",
            field);

        // Nothing is wrong with it yet, and it does not ask for the focus.
        Assert.DoesNotContain("aria-invalid", field);
        Assert.DoesNotContain("autofocus", field);
        Assert.DoesNotContain("country-error", field);

        // The first choice chooses none, and is the one that is chosen. Then every country, by
        // its code, with its name, in the order of the names: also those that the service is
        // not offered in, so that a visitor answers with where they live.
        var choices = Choice().Matches(field).Select(choice => (Code: choice.Groups[1].Value, Name: choice.Groups[3].Value)).ToList();

        Assert.Equal(("", "Choose a country"), choices[0]);
        Assert.Equal("<option value selected>Choose a country</option>", Choice().Match(field).Value);
        Assert.Equal(CountryNames.InOrder, choices.Skip(1));
        Assert.Single(Regex.Matches(field, " selected"));
        Assert.Contains("<option value=\"AQ\">Antarctica</option>", field);
        Assert.Contains("<option value=\"CI\">Côte d'Ivoire</option>", field);
    }

    [Fact]
    public async Task A_visitor_who_lives_where_the_service_is_offered_registers_on_the_form()
    {
        using var application = Restricting();
        using var client = application.CreateClient();

        var (path, sent) = await client.SubmitAsync(
            "/register",
            "register",
            [("Email", Ada), ("Password", AccountsApplication.Password), ("Country", Iceland), .. EveryDocumentAccepted]);

        Assert.Equal("/register/sent", path);
        Assert.Contains("<h1>Check your email</h1>", sent);
        Assert.Equal(Ada, Assert.Single(StoredUser.All(application.Database)).Email);
        Assert.Equal(Ada, Assert.Single(application.Email.Sent).To);
    }

    [Fact]
    public async Task The_form_tells_a_visitor_who_lives_in_a_restricted_country_that_the_service_is_not_offered_there()
    {
        using var application = Restricting();
        using var client = application.CreateClient();

        var (path, answer) = await client.SubmitAsync(
            "/register",
            "register",
            [("Email", Ada), ("Password", AccountsApplication.Password), ("Country", Antarctica), .. EveryDocumentAccepted]);

        var page = Read(answer);
        var field = Field().Match(page).Value;

        Assert.Equal("/register", path);
        Assert.Contains(
            "<p id=\"country-error\" class=\"field-error\">The service is not offered to people who live in Antarctica, " +
            "so no account can be registered.</p>",
            field);

        // The field is marked as wrong, names what says why before its hint, and has the focus:
        // nothing else of the form was refused.
        Assert.Contains(
            "<select id=\"country\" name=\"Country\" autocomplete=\"country\" required aria-invalid=\"true\" " +
            "aria-describedby=\"country-error country-hint\" autofocus>",
            field);
        Assert.Single(Regex.Matches(page, "autofocus"));
        Assert.DoesNotContain("email-error", page);
        Assert.DoesNotContain("password-error", page);
        Assert.DoesNotMatch("accept-[A-Za-z]+-error", page);

        // What was chosen is still chosen, and what was typed and ticked is still there.
        Assert.Contains("<option value=\"AQ\" selected>Antarctica</option>", field);
        Assert.Single(Regex.Matches(field, " selected"));
        Assert.Contains($"value=\"{Ada}\"", page);
        Assert.Matches("name=\"TermsOfService\"[^>]* checked", page);

        Assert.Empty(StoredUser.All(application.Database));
        Assert.Empty(application.Email.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Atlantis")]
    public async Task The_form_asks_a_visitor_who_chose_no_country_to_choose_one(string? posted)
    {
        using var application = Restricting();
        using var client = application.CreateClient();

        (string Name, string Value)[] country = posted is null ? [] : [("Country", posted)];

        var (path, answer) = await client.SubmitAsync(
            "/register", "register", [("Email", "no address"), ("Password", AccountsApplication.Password), .. country, .. EveryDocumentAccepted]);

        var page = Read(answer);
        var field = Field().Match(page).Value;

        Assert.Equal("/register", path);
        Assert.Contains("<p id=\"country-error\" class=\"field-error\">Choose the country that you live in.</p>", field);
        Assert.Contains("aria-invalid=\"true\" aria-describedby=\"country-error country-hint\">", field);
        Assert.Contains("<option value selected>Choose a country</option>", field);

        // What is not a country is not written back into the page.
        Assert.DoesNotContain("Atlantis", page);

        // The first field that was refused has the focus, in the order of the form: the address.
        Assert.Matches("<input id=\"email\"[^>]* autofocus", page);
        Assert.Single(Regex.Matches(page, "autofocus"));

        Assert.Empty(StoredUser.All(application.Database));
    }

    [Fact]
    public void The_countries_are_in_the_order_of_their_names_with_an_accent_read_as_its_letter()
    {
        var names = CountryNames.InOrder.Select(country => country.Name).ToList();

        Assert.Equal(names.OrderBy(Plain, StringComparer.OrdinalIgnoreCase), names);

        // So a visitor finds these where the alphabet has their letters.
        Assert.True(names.IndexOf("Afghanistan") < names.IndexOf("Åland Islands"));
        Assert.True(names.IndexOf("Åland Islands") < names.IndexOf("Albania"));
        Assert.True(names.IndexOf("Tunisia") < names.IndexOf("Türkiye"));
        Assert.True(names.IndexOf("Türkiye") < names.IndexOf("Turkmenistan"));

        // And the list is the one of the codes, each once.
        Assert.Equal(Countries.Codes.Count, names.Count);
    }

    /// <summary>A name without its accents: each letter as the alphabet has it.</summary>
    private static string Plain(string name) =>
        string.Concat(name.Normalize(NormalizationForm.FormD).Where(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));

    /// <summary>A page as a person reads it: what was written for markup is text again.</summary>
    private static string Read(string page) => WebUtility.HtmlDecode(page);

    // The field of the country: from its label to the end of what is under it.
    [GeneratedRegex("<label for=\"country\">.*?</select>(?:\\s*<p id=\"country-[a-z]+\"[^>]*>.*?</p>)*", RegexOptions.Singleline)]
    private static partial Regex Field();

    // A choice: its code, which the first has none of, whether it is the one chosen, and its name.
    [GeneratedRegex("<option value(?:=\"([A-Z]{2})\")?( selected)?>(.*?)</option>")]
    private static partial Regex Choice();
}
