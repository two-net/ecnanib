using System.Net;
using System.Net.Http;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Secrets;

namespace Ecnanib.Tests.Credentials;

/// <summary>FR-KEY-01, FR-KEY-03, FR-KEY-04 for AI provider keys.</summary>
public sealed class AiKeyTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task A_key_that_lists_models_is_saved_as_valid_and_shows_only_its_last_four_characters()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var before = app.Clock.GetUtcNow();

        var result = await app.AddAiAsync(userId, label: "Personal");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var credential = Assert.IsType<CredentialRecord>(result.Credential);
        Assert.Equal(
            (CredentialKind.Ai, CredentialApp.Provider, "Personal", "WXYZ", CredentialStatus.Valid),
            (credential.Kind, credential.Provider, credential.Label, credential.LastFour, credential.Status));
        Assert.Null(credential.Environment);
        Assert.Null(credential.SpotEnabled);
        Assert.InRange(credential.ValidatedAt!.Value, before, app.Clock.GetUtcNow());

        // Validated with the key itself, on its own credential's slot.
        Assert.Single(app.Ai.WithKey(CredentialApp.AiKey));

        var (apiKeyId, secretId) = await app.SecretIdsAsync(credential.Id);
        Assert.Null(secretId);
        Assert.Equal(CredentialApp.AiKey, await SecretApp.RevealAsync(app.App, apiKeyId));
        Assert.DoesNotContain("0123456789abcdef", await SecretApp.CiphertextHexAsync(app.ConnectionString, apiKeyId), StringComparison.Ordinal);

        var listed = Assert.Single(await app.Credentials.ListAsync(userId, Cancellation));
        Assert.Equal((credential.Id, credential.Label, credential.LastFour, credential.Status), (listed.Id, listed.Label, listed.LastFour, listed.Status));
        Assert.Equal("CredentialAdded", Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation)).Action);
    }

    [Fact]
    public async Task A_key_the_provider_refuses_is_not_stored()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-test-key"));
        var secrets = await app.CountAsync("secrets");

        var result = await app.AddAiAsync(Guid.NewGuid());

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal(0, await app.CountAsync("credentials"));
        Assert.Equal(secrets, await app.CountAsync("secrets"));
        Assert.Equal(0, await app.CountAsync("audit_events WHERE action LIKE 'Credential%'"));
    }

    [Fact]
    public async Task A_key_the_provider_knows_but_forbids_is_saved_with_insufficient_permissions()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.Forbidden, "permission_error", "no model access"));

        var credential = (await app.AddAiAsync(Guid.NewGuid())).Credential!;

        Assert.Equal((CredentialStatus.InsufficientPermissions, (CredentialProblem?)CredentialProblem.PermissionDenied), (credential.Status, credential.Problem));
    }

    [Fact]
    public async Task A_key_with_no_models_is_saved_with_insufficient_permissions()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Models(null));

        var credential = (await app.AddAiAsync(Guid.NewGuid())).Credential!;

        Assert.Equal((CredentialStatus.InsufficientPermissions, (CredentialProblem?)CredentialProblem.NoModels), (credential.Status, credential.Problem));
    }

    /// <summary>Unlike a Binance key, nothing about an AI key needs to be known before it's stored.</summary>
    [Fact]
    public async Task A_key_saved_while_the_provider_is_down_is_unverified()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "try later"));

        var result = await app.AddAiAsync(Guid.NewGuid());

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(CredentialStatus.Unverified, result.Credential!.Status);
        Assert.Null(result.Credential.Problem);
        Assert.Null(result.Credential.ValidatedAt);
    }

    [Theory]
    [InlineData("Anthropic ")]
    [InlineData("test")]
    [InlineData("Binance")]
    [InlineData("Missing")]
    public async Task Only_a_provider_with_an_enabled_adapter_is_accepted(string provider)
    {
        await using var app = new CredentialApp(postgres);

        var result = await app.AddAiAsync(Guid.NewGuid(), provider: provider);

        Assert.Equal(SaveOutcome.UnknownProvider, result.Outcome);
        Assert.Empty(app.Ai.Requests);
    }

    [Fact]
    public async Task A_disabled_provider_is_not_offered()
    {
        await using var app = new CredentialApp(postgres, new() { ["AiProviders:Second:Enabled"] = "false" });

        Assert.Contains(CredentialApp.Provider, app.Credentials.AiProviders);
        Assert.DoesNotContain("Second", app.Credentials.AiProviders);
        Assert.Equal(SaveOutcome.UnknownProvider, (await app.AddAiAsync(Guid.NewGuid(), provider: "Second")).Outcome);
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("sk-test-0123456789 abcdef")]
    [InlineData("sk-test-0123456789abcdef\n")]
    [InlineData("sk-tëst-0123456789abcdef")]
    public async Task A_key_that_is_not_shaped_like_one_is_refused_before_anything_is_sent(string key)
    {
        await using var app = new CredentialApp(postgres);

        var result = await app.AddAiAsync(Guid.NewGuid(), key);

        Assert.Equal(SaveOutcome.KeyMalformed, result.Outcome);
        Assert.Empty(app.Ai.Requests);
    }
}
