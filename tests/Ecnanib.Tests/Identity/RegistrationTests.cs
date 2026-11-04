using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-01, FR-AUTH-02, NFR-SEC-02, NFR-LEG-04 and EXT-UI-03.</summary>
public sealed class RegistrationTests(PostgresFixture postgres)
{
    private const string Blocked = "US";
    private const string Allowed = "DE";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registering_records_both_documents_with_their_versions_and_times()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        var connectionString = app.ConnectionString();
        var id = await AccountRows.IdAsync(connectionString, email, Cancellation);
        var rows = await Sql.ColumnAsync(
            connectionString,
            $"SELECT document || ' ' || version FROM legal_acceptances WHERE user_id = '{id}' AND accepted_at > now() - interval '1 minute' ORDER BY document",
            Cancellation);

        Assert.Equal(
            [$"RiskDisclosure {LegalDocuments.RiskDisclosureVersion}", $"TermsOfUse {LegalDocuments.TermsOfUseVersion}"],
            rows);
    }

    [Fact]
    public async Task A_password_is_stored_only_as_a_salted_memory_hard_hash()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);

        (await visitor.RegisterAsync(Visitor.NewEmail())).Dispose();

        var hash = await Sql.ScalarAsync<string>(app.ConnectionString(), "SELECT password_hash FROM users", Cancellation);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(Visitor.Password, hash, StringComparison.Ordinal);
    }

    /// <summary>A session here would show that the address was new, which is what the sign-in refusal keeps quiet.</summary>
    [Fact]
    public async Task Registering_leads_to_sign_in_and_doesnt_start_a_session()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);

        using var response = await visitor.RegisterAsync(Visitor.NewEmail());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sign-in?registered=true", Visitor.Target(response));
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith("ecnanib.session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_taken_address_gets_the_same_answer_and_changes_nothing()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var first = await visitor.RegisterAsync(email);
        using var second = await visitor.RegisterAsync(email.ToUpperInvariant(), "a different password entirely");

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(1, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));

        Assert.NotNull((await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation)).Principal);
        Assert.Null((await Accounts(app).SignInAsync(email, "a different password entirely", Visitor.AnyClient, Cancellation)).Principal);
    }

    /// <summary>EF logs a failed save as an Error, which would let visitors write a database row per attempt.</summary>
    [Fact]
    public async Task A_race_for_one_address_creates_one_account_and_logs_no_error()
    {
        await using var app = new Logging.LoggedApp(postgres.NewConnectionString(), AccountApp.Settings());
        var email = Visitor.NewEmail();

        var problems = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Accounts(app.App).RegisterAsync(
            new Registration
            {
                Email = email,
                Password = Visitor.Password,
                TermsOfUseVersion = LegalDocuments.TermsOfUseVersion,
                RiskDisclosureVersion = LegalDocuments.RiskDisclosureVersion,
            },
            Cancellation)));

        var connectionString = app.App.ConnectionString();
        var accounts = await AccountRows.CountAsync(connectionString, email, Cancellation);
        await app.StopAsync();

        Assert.All(problems, Assert.Empty);
        Assert.Equal(1, accounts);
        Assert.DoesNotContain(app.FileEvents(), entry => entry.GetProperty("Level").GetString() == "Error");
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task A_password_has_to_fit_the_limits(int length, bool accepted)
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, new string('x', length));

        Assert.Equal(accepted ? HttpStatusCode.Redirect : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(accepted ? 1 : 0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    [Theory]
    [MemberData(nameof(MalformedAddresses.Posted), MemberType = typeof(MalformedAddresses))]
    public async Task A_malformed_address_is_refused_and_writes_no_error(string description, string email)
    {
        await using var app = new Logging.LoggedApp(postgres.NewConnectionString(), AccountApp.Settings());
        using var visitor = Visitor.To(app.App);

        using var response = await visitor.RegisterAsync(email);
        var users = await Sql.ScalarAsync<long>(app.App.ConnectionString(), "SELECT count(*) FROM users", Cancellation);
        await app.StopAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{description} got {response.StatusCode}");
        Assert.Equal(0, users);
        Assert.DoesNotContain(app.FileEvents(), entry => entry.GetProperty("Level").GetString() == "Error");
    }

    [Theory]
    [MemberData(nameof(MalformedAddresses.All), MemberType = typeof(MalformedAddresses))]
    public async Task The_contract_refuses_a_malformed_address_too(string description, string email)
    {
        await using var app = AccountApp.Create(postgres);

        var problems = await Accounts(app).RegisterAsync(
            new Registration
            {
                Email = email,
                Password = Visitor.Password,
                TermsOfUseVersion = LegalDocuments.TermsOfUseVersion,
                RiskDisclosureVersion = LegalDocuments.RiskDisclosureVersion,
            },
            Cancellation);

        Assert.True(problems.Contains(RegistrationProblem.InvalidEmail), description);
    }

    [Fact]
    public async Task A_form_with_nothing_filled_in_is_refused()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);

        using var response = await visitor.SubmitAsync(
            "/register",
            Visitor.RegisterForm,
            ("Input.Email", string.Empty),
            ("Input.Password", string.Empty),
            ("Input.ConfirmPassword", string.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A checkbox posts a value that a visitor can replace, and form binding shouldn't turn that into a failure.</summary>
    [Fact]
    public async Task A_tick_with_a_nonsense_value_counts_as_unticked()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, Visitor.Password, ("Input.AcceptTermsOfUse", "banana"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    [Fact]
    public async Task The_operator_can_raise_the_minimum_password_length()
    {
        await using var app = AccountApp.Create(postgres, new() { ["Security:PasswordMinLength"] = "20" });
        using var visitor = Visitor.To(app);

        using var response = await visitor.RegisterAsync(Visitor.NewEmail(), new string('x', 19));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at least 20 characters", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_form_keeps_the_email_and_never_the_password()
    {
        const string Canary = "canary-password-1234";

        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, Canary, ("Input.ConfirmPassword", "something else altogether"));
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("match", page, StringComparison.Ordinal);
        Assert.Contains(email, page, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, page, StringComparison.Ordinal);
        Assert.Equal(0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    [Theory]
    [InlineData("Input.AcceptTermsOfUse")]
    [InlineData("Input.AcceptRiskDisclosure")]
    public async Task Both_documents_have_to_be_accepted(string unticked)
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, Visitor.Password, (unticked, "false"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    /// <summary>A deployment can land while a form is open, and nobody accepted the text they never saw.</summary>
    [Fact]
    public async Task A_form_showing_an_old_version_is_shown_again_with_the_new_one()
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, Visitor.Password, ("Input.TermsOfUseVersion", "an older version"));
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("have changed", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{LegalDocuments.TermsOfUseVersion}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(" checked", page, StringComparison.Ordinal);
        Assert.Equal(0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    [Theory]
    [InlineData("not financial advice")]
    [InlineData("Automated cryptocurrency trading can lose money")]
    [InlineData("not affiliated with or endorsed by Binance")]
    public async Task The_mandatory_notices_are_on_the_form(string notice)
    {
        await using var app = AccountApp.Create(postgres);
        using var visitor = Visitor.To(app);

        Assert.Contains(notice, await visitor.PageAsync("/register"), StringComparison.Ordinal);
    }

    /// <summary>NFR-LEG-03: with nothing blocked, the answer would serve no purpose.</summary>
    [Fact]
    public async Task The_form_asks_for_a_country_only_when_one_is_blocked()
    {
        await using var open = AccountApp.Create(postgres);
        await using var blocking = AccountApp.Create(postgres, new() { ["Legal:BlockedCountries:0"] = Blocked });
        using var first = Visitor.To(open);
        using var second = Visitor.To(blocking);

        Assert.DoesNotContain("Input.Country", await first.PageAsync("/register"), StringComparison.Ordinal);
        Assert.Contains("Input.Country", await second.PageAsync("/register"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Blocked, false)]
    [InlineData(Allowed, true)]
    [InlineData("", false)]
    [InlineData("ZZ", false)]
    public async Task A_declared_country_is_checked_against_the_list(string country, bool accepted)
    {
        await using var app = AccountApp.Create(postgres, new() { ["Legal:BlockedCountries:0"] = Blocked });
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        using var response = await visitor.RegisterAsync(email, Visitor.Password, ("Input.Country", country));

        Assert.Equal(accepted ? HttpStatusCode.Redirect : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(accepted ? 1 : 0, await AccountRows.CountAsync(app.ConnectionString(), email, Cancellation));
    }

    /// <summary>NFR-LEG-03: it's only needed for the check, and SRS 6.1 has no such field.</summary>
    [Fact]
    public async Task A_declared_country_isnt_stored()
    {
        await using var app = AccountApp.Create(postgres, new() { ["Legal:BlockedCountries:0"] = Blocked });
        using var visitor = Visitor.To(app);

        (await visitor.RegisterAsync(Visitor.NewEmail(), Visitor.Password, ("Input.Country", Allowed))).Dispose();

        var columns = await Sql.ColumnAsync(
            app.ConnectionString(),
            "SELECT column_name::text FROM information_schema.columns WHERE table_name IN ('users', 'legal_acceptances')",
            Cancellation);

        Assert.DoesNotContain(columns, column => column.Contains("country", StringComparison.Ordinal));
    }

    [Fact]
    public void The_country_list_has_every_ISO_code_and_a_name_for_each()
    {
        Assert.Equal(250, Countries.All.Count);
        Assert.Equal(Countries.All.Count, Countries.All.Select(country => country.Code).Distinct().Count());
        Assert.All(Countries.All, country => Assert.Matches("^[A-Z]{2}$", country.Code));
        Assert.All(Countries.All, country => Assert.False(string.IsNullOrWhiteSpace(country.Name)));
        Assert.True(Countries.IsKnown("GB"));
        Assert.False(Countries.IsKnown("UK"));
    }

    private static IAccounts Accounts(AppFactory app) => app.Services.GetRequiredService<IAccounts>();
}
