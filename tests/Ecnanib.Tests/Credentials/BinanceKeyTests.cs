using System.Net;
using System.Net.Http;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Secrets;
using NSec.Cryptography;

namespace Ecnanib.Tests.Credentials;

/// <summary>FR-KEY-01, FR-KEY-02, FR-KEY-04, FR-KEY-05, FR-KEY-06, AC-04, and FR-AUTH-05 for exchange keys.</summary>
public sealed class BinanceKeyTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    /// <summary>AC-04. Least privilege at rest: a key that could withdraw is never stored, even for a while.</summary>
    [Fact]
    public async Task A_key_with_withdrawals_enabled_is_rejected_and_nothing_is_stored()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(withdrawals: true);
        var secrets = await app.CountAsync("secrets");

        var result = await app.AddBinanceAsync(Guid.NewGuid());

        Assert.Equal(SaveOutcome.WithdrawalsEnabled, result.Outcome);
        Assert.Null(result.Credential);
        Assert.Equal(0, await app.CountAsync("credentials"));
        Assert.Equal(secrets, await app.CountAsync("secrets"));
        Assert.Empty(app.Binance.To(CredentialApp.SpotAccountPath));
    }

    [Fact]
    public async Task A_Live_key_is_validated_with_its_restrictions_and_its_accounts()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var before = app.Clock.GetUtcNow();

        var result = await app.AddBinanceAsync(userId, label: "Main account");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var credential = Assert.IsType<CredentialRecord>(result.Credential);
        Assert.Equal(
            (CredentialKind.Exchange, CredentialProviders.Binance, (TradingEnvironment?)TradingEnvironment.Live, (BinanceKeyType?)BinanceKeyType.Hmac),
            (credential.Kind, credential.Provider, credential.Environment, credential.KeyType));
        Assert.Equal(("Main account", "Eh8A", CredentialStatus.Valid), (credential.Label, credential.LastFour, credential.Status));
        Assert.Equal((true, true, true), (credential.SpotEnabled, credential.FuturesEnabled, credential.IpRestricted));
        Assert.InRange(credential.ValidatedAt!.Value, before, app.Clock.GetUtcNow());
        Assert.Null(credential.LastUsedAt);

        Assert.Equal(FakeBinance.SpotLive, Assert.Single(app.Binance.To(CredentialApp.RestrictionsPath)).Host);
        Assert.Equal(FakeBinance.SpotLive, Assert.Single(app.Binance.To(CredentialApp.SpotAccountPath)).Host);
        Assert.Equal(FakeBinance.FuturesLive, Assert.Single(app.Binance.To(CredentialApp.FuturesAccountPath)).Host);

        // Both halves are secrets, each in a row of its own, and the credential keeps only their IDs.
        var (apiKeyId, secretId) = await app.SecretIdsAsync(credential.Id);
        Assert.Equal(ExchangeHarness.ExampleApiKey, await SecretApp.RevealAsync(app.App, apiKeyId));
        Assert.Equal(ExchangeHarness.ExampleSecret, await SecretApp.RevealAsync(app.App, secretId!.Value));

        var record = Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation));
        Assert.Equal(("CredentialAdded", "Credential", credential.Id.ToString()), (record.Action, record.TargetType, record.TargetId));
        Assert.Contains("Main account", record.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, record.Summary, StringComparison.Ordinal);
    }

    public static TheoryData<bool, bool, CredentialStatus> Permissions => new()
    {
        { true, true, CredentialStatus.Valid },
        { true, false, CredentialStatus.Valid },
        { false, true, CredentialStatus.Valid },
        { false, false, CredentialStatus.InsufficientPermissions },
    };

    /// <summary>FR-KEY-02: each market is recorded, and a key that can trade on neither is kept to be fixed on Binance.</summary>
    [Theory]
    [MemberData(nameof(Permissions))]
    public async Task Which_markets_a_key_can_trade_is_recorded(bool spot, bool futures, CredentialStatus status)
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(spot: spot, futures: futures);

        var credential = (await app.AddBinanceAsync(Guid.NewGuid())).Credential!;

        Assert.Equal((spot, futures, status), (credential.SpotEnabled, credential.FuturesEnabled, credential.Status));
        Assert.Equal(status == CredentialStatus.Valid ? null : CredentialProblem.NoTradingPermission, credential.Problem);

        // A key Binance didn't enable for Futures isn't tried there.
        Assert.Equal(futures ? 1 : 0, app.Binance.To(CredentialApp.FuturesAccountPath).Count);
    }

    /// <summary>Binance says a key made before the Futures account was opened can't use the Futures API.</summary>
    [Fact]
    public async Task Futures_counts_only_when_the_Futures_account_takes_the_key()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        app.Binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => CredentialApp.Refusal());

        var credential = (await app.AddBinanceAsync(Guid.NewGuid())).Credential!;

        Assert.Equal((true, false, CredentialStatus.Valid), (credential.SpotEnabled, credential.FuturesEnabled, credential.Status));
    }

    /// <summary>FR-KEY-05: the warning, and the addresses to restrict it to, come from the IP restriction.</summary>
    [Fact]
    public async Task A_key_without_an_IP_restriction_is_saved_with_the_warning_and_the_server_s_addresses()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts(ipRestrict: false);

        var credential = (await app.AddBinanceAsync(Guid.NewGuid())).Credential!;

        Assert.Equal((false, CredentialStatus.Valid), (credential.IpRestricted, credential.Status));
        Assert.Equal([CredentialApp.EgressAddress, CredentialApp.EgressAddressV6], app.Credentials.EgressIpAddresses);
    }

    /// <summary>Binance documents restrictions on its Live wallet API only, so a Demo key is read from its accounts.</summary>
    [Fact]
    public async Task A_Demo_key_is_validated_by_its_accounts_without_asking_for_restrictions()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();

        var credential = (await app.AddBinanceAsync(Guid.NewGuid(), TradingEnvironment.Demo)).Credential!;

        Assert.Equal((CredentialStatus.Valid, true, true), (credential.Status, credential.SpotEnabled, credential.FuturesEnabled));
        Assert.Null(credential.IpRestricted);
        Assert.Empty(app.Binance.To(CredentialApp.RestrictionsPath));
        Assert.Equal(FakeBinance.SpotDemo, Assert.Single(app.Binance.To(CredentialApp.SpotAccountPath)).Host);
        Assert.Equal(FakeBinance.FuturesDemo, Assert.Single(app.Binance.To(CredentialApp.FuturesAccountPath)).Host);
    }

    [Fact]
    public async Task A_Demo_key_that_only_one_market_takes_is_enabled_for_that_one()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        app.Binance.On(HttpMethod.Get, CredentialApp.SpotAccountPath, _ => CredentialApp.Refusal());

        var credential = (await app.AddBinanceAsync(Guid.NewGuid(), TradingEnvironment.Demo)).Credential!;

        Assert.Equal((CredentialStatus.Valid, false, true), (credential.Status, credential.SpotEnabled, credential.FuturesEnabled));
    }

    [Theory]
    [InlineData(TradingEnvironment.Live)]
    [InlineData(TradingEnvironment.Demo)]
    public async Task A_key_Binance_refuses_is_not_stored(TradingEnvironment environment)
    {
        await using var app = new CredentialApp(postgres);
        app.Binance.On(HttpMethod.Get, CredentialApp.RestrictionsPath, _ => CredentialApp.Refusal());
        app.Binance.On(HttpMethod.Get, CredentialApp.SpotAccountPath, _ => CredentialApp.Refusal());
        app.Binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => CredentialApp.Refusal());

        var result = await app.AddBinanceAsync(Guid.NewGuid(), environment);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal(0, await app.CountAsync("credentials"));
    }

    /// <summary>A key whose withdrawal permission was never read isn't stored, unlike an AI key (AC-04).</summary>
    [Fact]
    public async Task Nothing_is_stored_when_Binance_cannot_be_reached()
    {
        await using var app = new CredentialApp(postgres);
        app.Binance.On(
            HttpMethod.Get, CredentialApp.RestrictionsPath, _ => FakeBinance.Json("""{"code":-1001,"msg":"Internal error."}""", HttpStatusCode.ServiceUnavailable));

        var result = await app.AddBinanceAsync(Guid.NewGuid());

        Assert.Equal(SaveOutcome.Unreachable, result.Outcome);
        Assert.Equal(0, await app.CountAsync("credentials"));
    }

    /// <summary>FR-KEY-06. Futures counts only if a request signed with it succeeded there.</summary>
    [Fact]
    public async Task An_Ed25519_key_is_validated_by_signing_with_it()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        using var privateKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var pem = Encoding.ASCII.GetString(privateKey.Export(KeyBlobFormat.PkixPrivateKeyText));

        var credential = (await app.AddBinanceAsync(Guid.NewGuid(), TradingEnvironment.Demo, secret: pem, type: BinanceKeyType.Ed25519)).Credential!;

        Assert.Equal((CredentialStatus.Valid, (BinanceKeyType?)BinanceKeyType.Ed25519, true), (credential.Status, credential.KeyType, credential.FuturesEnabled));
        var request = Assert.Single(app.Binance.To(CredentialApp.FuturesAccountPath));
        Assert.True(SignatureAlgorithm.Ed25519.Verify(
            privateKey.PublicKey, Encoding.ASCII.GetBytes(request.SignedPayload), Convert.FromBase64String(request.Parameter("signature")!)));
    }

    public static TheoryData<string, string, BinanceKeyType> MalformedKeys => new()
    {
        { "short", ExchangeHarness.ExampleSecret, BinanceKeyType.Hmac },
        { ExchangeHarness.ExampleApiKey + " ", ExchangeHarness.ExampleSecret, BinanceKeyType.Hmac },
        { ExchangeHarness.ExampleApiKey, "a secret with spaces in it", BinanceKeyType.Hmac },
        { ExchangeHarness.ExampleApiKey, ExchangeHarness.ExampleSecret, BinanceKeyType.Ed25519 },
    };

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public async Task A_key_that_is_not_shaped_like_one_is_refused_before_anything_is_sent(string apiKey, string secret, BinanceKeyType type)
    {
        await using var app = new CredentialApp(postgres);

        var result = await app.AddBinanceAsync(Guid.NewGuid(), apiKey: apiKey, secret: secret, type: type);

        Assert.Equal(SaveOutcome.KeyMalformed, result.Outcome);
        Assert.Empty(app.Binance.Requests);
    }

    /// <summary>A PEM block that isn't a key passes the shape check, and the signer refuses it with nothing sent.</summary>
    [Fact]
    public async Task A_private_key_the_signer_cannot_read_is_refused()
    {
        await using var app = new CredentialApp(postgres);
        const string Pem = "-----BEGIN PRIVATE KEY-----\nbm90IGEga2V5\n-----END PRIVATE KEY-----";

        var result = await app.AddBinanceAsync(Guid.NewGuid(), TradingEnvironment.Demo, secret: Pem, type: BinanceKeyType.Ed25519);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Empty(app.Binance.To(CredentialApp.SpotAccountPath));
    }

    /// <summary>FR-AUTH-05. The code comes first, so a stolen session can't use the server to try keys.</summary>
    [Fact]
    public async Task With_two_factor_on_a_code_is_needed_before_Binance_hears_of_the_key()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        using var visitor = Visitor.To(app.App);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);
        var userId = await AccountRows.IdAsync(app.ConnectionString, account.Email, Cancellation);

        var missing = await app.AddBinanceAsync(userId);
        var wrong = await app.AddBinanceAsync(userId, code: "000000");

        Assert.Equal((SaveOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (missing.Outcome, missing.TwoFactor));
        Assert.Equal((SaveOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (wrong.Outcome, wrong.TwoFactor));
        Assert.Empty(app.Binance.Requests);

        var right = await app.AddBinanceAsync(userId, code: app.Clock.NextCode(account.Key));

        Assert.Equal(SaveOutcome.Saved, right.Outcome);
    }

    [Fact]
    public async Task A_label_is_needed_and_kept_to_plain_text()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();

        Assert.Equal(SaveOutcome.LabelInvalid, (await app.AddBinanceAsync(Guid.NewGuid(), label: " \t ")).Outcome);
        Assert.Equal(SaveOutcome.LabelInvalid, (await app.AddBinanceAsync(Guid.NewGuid(), label: new string('a', 61))).Outcome);
        Assert.Empty(app.Binance.Requests);

        var credential = (await app.AddBinanceAsync(Guid.NewGuid(), label: " Main\u0000 account\n")).Credential!;

        Assert.Equal("Main account", credential.Label);
    }
}
