using System.Net;
using System.Net.Http;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Secrets;

namespace Ecnanib.Tests.Credentials;

/// <summary>FR-KEY-01: rotating keeps the credential, its ID and its label, and replaces its secrets.</summary>
public sealed class RotationTests(PostgresFixture postgres)
{
    private const string NewApiKey = "NEWkeyPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju9NEW1";
    private const string NewSecret = "NEWsecretSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fNEW";

    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task A_new_AI_key_replaces_the_old_one_and_its_secret_is_deleted()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        var (oldKeyId, _) = await app.SecretIdsAsync(added.Id);

        var result = await app.RotateAiAsync(userId, added.Id);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal((added.Id, added.Label, "ABCD", CredentialStatus.Valid), (result.Credential!.Id, result.Credential.Label, result.Credential.LastFour, result.Credential.Status));

        var (newKeyId, _) = await app.SecretIdsAsync(added.Id);
        Assert.NotEqual(oldKeyId, newKeyId);
        Assert.Equal(CredentialApp.OtherAiKey, await SecretApp.RevealAsync(app.App, newKeyId));
        Assert.Equal(0, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, oldKeyId, Cancellation));

        var record = (await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation))[^1];
        Assert.Equal("CredentialRotated", record.Action);
        Assert.Contains("WXYZ", record.Summary, StringComparison.Ordinal);
        Assert.Contains("ABCD", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_key_leaves_the_credential_as_it_was()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        var before = await app.SecretIdsAsync(added.Id);
        var stored = await app.Credentials.FindAsync(userId, added.Id, Cancellation);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, request =>
            request.Key == CredentialApp.AiKey
                ? FakeAiProvider.Models(null, "model-a")
                : FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid"));

        var result = await app.RotateAiAsync(userId, added.Id);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal(before, await app.SecretIdsAsync(added.Id));
        Assert.Equal(stored, await app.Credentials.FindAsync(userId, added.Id, Cancellation));
    }

    [Fact]
    public async Task A_Binance_key_is_replaced_by_one_for_the_same_account()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(userId)).Credential!;
        var (oldKeyId, oldSecretId) = await app.SecretIdsAsync(added.Id);

        var result = await app.RotateBinanceAsync(userId, added.Id, NewApiKey, NewSecret);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal("NEW1", result.Credential!.LastFour);
        var (newKeyId, newSecretId) = await app.SecretIdsAsync(added.Id);
        Assert.Equal(NewSecret, await SecretApp.RevealAsync(app.App, newSecretId!.Value));
        Assert.Equal(0, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, oldKeyId, Cancellation));
        Assert.Equal(0, await TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, oldSecretId!.Value, Cancellation));
        Assert.NotEqual(oldKeyId, newKeyId);
    }

    /// <summary>Agents' orders and positions live on the account, so a rotation can't quietly move them.</summary>
    [Fact]
    public async Task A_key_for_another_Binance_account_is_refused()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(userId)).Credential!;
        var before = await app.SecretIdsAsync(added.Id);
        app.BinanceAccepts(uid: 42);

        var result = await app.RotateBinanceAsync(userId, added.Id, NewApiKey, NewSecret);

        Assert.Equal(SaveOutcome.DifferentAccount, result.Outcome);
        Assert.Equal(before, await app.SecretIdsAsync(added.Id));
    }

    [Fact]
    public async Task A_rotation_needs_the_right_kind_of_key()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var ai = (await app.AddAiAsync(userId)).Credential!;
        var binance = (await app.AddBinanceAsync(userId)).Credential!;

        Assert.Equal(SaveOutcome.NotFound, (await app.RotateBinanceAsync(userId, ai.Id, NewApiKey, NewSecret)).Outcome);
        Assert.Equal(SaveOutcome.NotFound, (await app.RotateAiAsync(userId, binance.Id)).Outcome);
    }

    /// <summary>A deletion while the new key is checked wins, and the rotation leaves no secrets behind.</summary>
    [Fact]
    public async Task A_credential_deleted_during_a_rotation_stays_deleted()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(userId)).Credential!;
        var secrets = await app.CountAsync("secrets");

        app.Binance.On(HttpMethod.Get, CredentialApp.RestrictionsPath, async (request, _) =>
        {
            await app.Credentials.DeleteAsync(userId, added.Id, Cancellation);
            return FakeBinance.Json(BinanceSamples.ApiRestrictions());
        });

        var result = await app.RotateBinanceAsync(userId, added.Id, NewApiKey, NewSecret);

        Assert.Equal(SaveOutcome.Changed, result.Outcome);
        Assert.Equal(0, await app.CountAsync("credentials"));
        Assert.Equal(secrets - 2, await app.CountAsync("secrets"));
    }

    [Fact]
    public async Task With_two_factor_on_replacing_a_Binance_key_needs_a_code()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        using var visitor = Visitor.To(app.App);
        var account = await TwoFactorSteps.SignUpWithTwoFactorAsync(visitor, app.Clock);
        var userId = await AccountRows.IdAsync(app.ConnectionString, account.Email, Cancellation);
        var added = (await app.AddBinanceAsync(userId, code: app.Clock.NextCode(account.Key))).Credential!;
        var requests = app.Binance.Requests.Count;

        var refused = await app.RotateBinanceAsync(userId, added.Id, NewApiKey, NewSecret, code: "000000");

        Assert.Equal(SaveOutcome.TwoFactorRefused, refused.Outcome);
        Assert.Equal(requests, app.Binance.Requests.Count);

        var rotated = await app.RotateBinanceAsync(userId, added.Id, NewApiKey, NewSecret, code: app.Clock.NextCode(account.Key));

        Assert.Equal(SaveOutcome.Saved, rotated.Outcome);
    }
}
