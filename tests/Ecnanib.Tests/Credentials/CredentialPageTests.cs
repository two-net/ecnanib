using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// FR-KEY-01 and FR-KEY-03 through the pages: secrets go in through static forms, and no response ever
/// carries one back, after a refusal or a success.
/// </summary>
public sealed class CredentialPageTests(PostgresFixture postgres)
{
    private const string ListPage = "/credentials";
    private const string AddPage = "/credentials/add";
    private const string AddAiForm = "add-ai-key";
    private const string AddBinanceForm = "add-binance-key";

    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task An_AI_key_added_through_the_form_is_listed_by_its_last_four_characters_only()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(
            AddPage, AddAiForm, ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", $" {CredentialApp.AiKey}\n"));

        Assert.Equal($"{ListPage}?saved=added", Visitor.Target(response));

        var page = WebUtility.HtmlDecode(await visitor.PageAsync($"{ListPage}?saved=added"));

        Assert.Contains("Key saved.", page, StringComparison.Ordinal);
        Assert.Contains("Personal", page, StringComparison.Ordinal);
        Assert.Contains("…WXYZ", page, StringComparison.Ordinal);
        Assert.Contains("Valid", page, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialApp.AiKey, page, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialApp.AiKey[..20], page, StringComparison.Ordinal);
    }

    /// <summary>FR-CAT-02, FR-CAT-03: a new key's models are synced, and the page offers to sync them again.</summary>
    [Fact]
    public async Task An_AI_key_shows_how_many_models_it_can_use_and_the_page_offers_a_sync()
    {
        await using var app = new CredentialApp(postgres, observers: true);
        app.Ai.On(System.Net.Http.HttpMethod.Get, CredentialApp.ModelsPath, _ => Ai.FakeAiProvider.Models(null, "model-a", "model-b"));
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();
        var before = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        (await visitor.SubmitAsync(
            AddPage, AddAiForm, ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", CredentialApp.AiKey))).Dispose();
        await Logging.LoggedApp.UntilAsync(async () => await Sql.ScalarAsync<long>(
            app.ConnectionString, "SELECT count(*) FROM job_runs WHERE job_name = 'catalog-sync-credential' AND finished_at IS NOT NULL", Cancellation) == 1);
        var page = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        Assert.DoesNotContain("Sync models", before, StringComparison.Ordinal);
        Assert.Contains("Sync models", page, StringComparison.Ordinal);
        Assert.Contains(">Models</dt>", page, StringComparison.Ordinal);
        Assert.Contains("2 available, synced ", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_AI_key_is_not_written_back_into_the_page()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(System.Net.Http.HttpMethod.Get, CredentialApp.ModelsPath, _ => Ai.FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid"));
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(
            AddPage, AddAiForm, ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", CredentialApp.AiKey));
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Test refused this key, so it wasn't saved.", page, StringComparison.Ordinal);
        Assert.Contains("value=\"Personal\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialApp.AiKey, page, StringComparison.Ordinal);
    }

    /// <summary>AC-04 on the page: the user is told why, and neither half of the key comes back.</summary>
    [Fact]
    public async Task A_Binance_key_that_can_withdraw_is_refused_on_the_page_without_writing_it_back()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(withdrawals: true);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(
            AddPage,
            AddBinanceForm,
            ("Input.Environment", "Live"),
            ("Input.Label", "Main account"),
            ("Input.ApiKey", ExchangeHarness.ExampleApiKey),
            ("Input.Secret", ExchangeHarness.ExampleSecret));
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("This key can withdraw funds, so it wasn't saved.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, page, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleSecret, page, StringComparison.Ordinal);
        Assert.Equal(0, await app.CountAsync("credentials"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(ExchangeHarness.ExampleSecret, "-----BEGIN PRIVATE KEY-----")]
    public async Task A_Binance_key_needs_its_secret_or_its_private_key_but_not_both(string secret, string privateKey)
    {
        await using var app = new CredentialApp(postgres);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(
            AddPage,
            AddBinanceForm,
            ("Input.Environment", "Demo"),
            ("Input.Label", "Demo"),
            ("Input.ApiKey", ExchangeHarness.ExampleApiKey),
            ("Input.Secret", secret),
            ("Input.PrivateKey", privateKey));
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Contains("Fill in the secret key or the private key, but not both.", page, StringComparison.Ordinal);
        Assert.Empty(app.Binance.Requests);
    }

    [Fact]
    public async Task A_Binance_key_without_an_IP_restriction_is_listed_with_the_addresses_to_allow()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(ipRestrict: false);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        using var response = await visitor.SubmitAsync(
            AddPage,
            AddBinanceForm,
            ("Input.Environment", "Demo"),
            ("Input.Label", "Demo account"),
            ("Input.ApiKey", ExchangeHarness.ExampleApiKey),
            ("Input.Secret", ExchangeHarness.ExampleSecret));

        Assert.Equal($"{ListPage}?saved=added", Visitor.Target(response));

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        // Demo: Binance doesn't document restrictions there, so there's nothing to warn about.
        Assert.Contains("Binance Demo", page, StringComparison.Ordinal);
        Assert.DoesNotContain("This key works from any IP address.", page, StringComparison.Ordinal);

        using var live = await visitor.SubmitAsync(
            AddPage,
            AddBinanceForm,
            ("Input.Environment", "Live"),
            ("Input.Label", "Live account"),
            ("Input.ApiKey", ExchangeHarness.ExampleApiKey),
            ("Input.Secret", ExchangeHarness.ExampleSecret));
        page = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        Assert.Contains("This key works from any IP address.", page, StringComparison.Ordinal);
        Assert.Contains(CredentialApp.EgressAddress, page, StringComparison.Ordinal);
        Assert.Contains(CredentialApp.EgressAddressV6, page, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleSecret, page, StringComparison.Ordinal);
    }

    /// <summary>FR-AUTH-05: the form asks for a code only when the account has two-factor on.</summary>
    [Fact]
    public async Task The_code_field_appears_only_with_two_factor_on()
    {
        await using var app = new CredentialApp(postgres);
        using var without = Visitor.To(app.App);
        using var with = Visitor.To(app.App);
        await without.SignUpAsync();
        await TwoFactorSteps.SignUpWithTwoFactorAsync(with, app.Clock);

        Assert.DoesNotContain("Input.TwoFactorCode", await without.PageAsync(AddPage), StringComparison.Ordinal);
        Assert.Contains("Input.TwoFactorCode", await with.PageAsync(AddPage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_is_renamed_on_its_own_page_and_someone_else_s_page_is_not_found()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        using var owner = Visitor.To(app.App);
        using var stranger = Visitor.To(app.App);
        var email = await owner.SignUpAsync();
        await stranger.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var added = (await app.AddAiAsync(userId, label: "Personal")).Credential!;
        var page = $"{ListPage}/{added.Id}";

        Assert.Contains("Personal", await owner.PageAsync(page), StringComparison.Ordinal);

        using (var response = await stranger.Client.GetAsync(page, Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Personal", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        }

        using (var response = await stranger.Client.GetAsync($"{ListPage}/{Guid.NewGuid()}", Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var renamed = await owner.SubmitAsync(page, "rename-key", ("Input.Label", "Work"));

        Assert.Equal($"{ListPage}?saved=renamed", Visitor.Target(renamed));
        Assert.Equal("Work", (await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.Label);
    }

    [Fact]
    public async Task A_key_is_replaced_on_its_own_page()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        using var visitor = Visitor.To(app.App);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var added = (await app.AddAiAsync(userId)).Credential!;

        using var response = await visitor.SubmitAsync($"{ListPage}/{added.Id}", "rotate-key", ("Input.ApiKey", CredentialApp.OtherAiKey));

        Assert.Equal($"{ListPage}?saved=rotated", Visitor.Target(response));
        Assert.Equal("ABCD", (await app.Credentials.FindAsync(userId, added.Id, Cancellation))!.LastFour);
    }

    /// <summary>FR-AUD-03 with FR-AUD-02's summary: the activity page says which key, without its secret.</summary>
    [Fact]
    public async Task The_activity_page_says_which_key_was_added()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();
        (await visitor.SubmitAsync(
            AddPage, AddAiForm, ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", CredentialApp.AiKey))).Dispose();

        var page = WebUtility.HtmlDecode(await visitor.PageAsync("/account/activity"));

        Assert.Contains("Added an API key", page, StringComparison.Ordinal);
        Assert.Contains(">Label</dt>", page, StringComparison.Ordinal);
        Assert.Contains("Personal", page, StringComparison.Ordinal);
        Assert.Contains(">Status</dt>", page, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialApp.AiKey, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_menu_links_to_the_keys()
    {
        await using var app = new CredentialApp(postgres);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        Assert.Contains($"href=\"{ListPage}\"", await visitor.PageAsync("/account"), StringComparison.Ordinal);
    }
}
