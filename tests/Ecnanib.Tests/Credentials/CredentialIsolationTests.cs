using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Credentials;

/// <summary>NFR-SEC-04: nobody can read or change another user's credentials, through any call.</summary>
public sealed class CredentialIsolationTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task Another_user_can_neither_see_nor_change_a_credential()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        app.BinanceAccepts();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var ai = (await app.AddAiAsync(owner)).Credential!;
        var binance = (await app.AddBinanceAsync(owner)).Credential!;
        var before = await app.Credentials.ListAsync(owner, Cancellation);
        var secrets = (await app.SecretIdsAsync(ai.Id), await app.SecretIdsAsync(binance.Id));
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var requests = (app.Ai.Requests.Count, app.Binance.Requests.Count);

        Assert.Empty(await app.Credentials.ListAsync(stranger, Cancellation));
        Assert.Null(await app.Credentials.FindAsync(stranger, ai.Id, Cancellation));
        Assert.Equal(RenameOutcome.NotFound, await app.Credentials.RenameAsync(stranger, ai.Id, "Mine now", Cancellation));
        Assert.Equal(ValidationOutcome.NotFound, (await app.Credentials.ValidateAsync(stranger, binance.Id, Cancellation)).Outcome);
        Assert.Equal(SaveOutcome.NotFound, (await app.RotateAiAsync(stranger, ai.Id)).Outcome);
        Assert.Equal(SaveOutcome.NotFound, (await app.RotateBinanceAsync(stranger, binance.Id)).Outcome);
        Assert.False(await app.Credentials.DeleteAsync(stranger, ai.Id, Cancellation));
        Assert.False(await app.Credentials.DeleteAsync(stranger, binance.Id, Cancellation));
        await Assert.ThrowsAsync<CredentialUnusableException>(() => app.Keys.OpenAiKeyAsync(stranger, ai.Id, Cancellation));
        await Assert.ThrowsAsync<CredentialUnusableException>(() => app.Keys.OpenAiKeyToListModelsAsync(stranger, ai.Id, Cancellation));
        Assert.Empty(await app.Keys.ListAiAsync(stranger, Cancellation));
        await Assert.ThrowsAsync<CredentialUnusableException>(() => app.Keys.OpenExchangeKeyAsync(stranger, binance.Id, Market.Spot, Cancellation));

        Assert.Equal(before, await app.Credentials.ListAsync(owner, Cancellation));
        Assert.Equal(secrets, (await app.SecretIdsAsync(ai.Id), await app.SecretIdsAsync(binance.Id)));
        Assert.Equal(requests, (app.Ai.Requests.Count, app.Binance.Requests.Count));
    }

    [Fact]
    public async Task Deleting_a_credential_deletes_its_secrets_and_is_audited()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var owner = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(owner, label: "Old account")).Credential!;
        var (apiKeyId, secretId) = await app.SecretIdsAsync(added.Id);

        Assert.True(await app.Credentials.DeleteAsync(owner, added.Id, Cancellation));

        Assert.Empty(await app.Credentials.ListAsync(owner, Cancellation));
        Assert.Equal(0, await Identity.TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, apiKeyId, Cancellation));
        Assert.Equal(0, await Identity.TwoFactorRows.SecretsWithIdAsync(app.ConnectionString, secretId!.Value, Cancellation));
        var record = (await Audit.AuditRows.OfUserAsync(app.ConnectionString, owner, Cancellation))[^1];
        Assert.Equal("CredentialDeleted", record.Action);
        Assert.Contains("Old account", record.Summary, StringComparison.Ordinal);
        Assert.False(await app.Credentials.DeleteAsync(owner, added.Id, Cancellation));
    }
}
