using System.Net;
using System.Net.Http;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Credentials;

/// <summary>FR-KEY-04: validation on demand, and FR-AUD-01's record of it.</summary>
public sealed class ValidationTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task A_key_revoked_since_it_was_added_becomes_invalid_and_the_check_is_audited()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "revoked"));
        app.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);

        Assert.Equal(ValidationOutcome.Checked, result.Outcome);
        Assert.Equal((CredentialStatus.Invalid, (CredentialProblem?)CredentialProblem.Refused), (result.Credential!.Status, result.Credential.Problem));
        Assert.True(result.Credential.ValidatedAt > added.ValidatedAt);
        Assert.Null(result.Credential.LastUsedAt);

        var record = (await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation))[^1];
        Assert.Equal(("CredentialValidated", added.Id.ToString()), (record.Action, record.TargetId));
        Assert.Contains("\"before\": \"Valid\"", record.Summary, StringComparison.Ordinal);
        Assert.Contains("\"after\": \"Invalid\"", record.Summary, StringComparison.Ordinal);
    }

    /// <summary>A circuit never reaches the HTTP limiter, and Binance's weight is shared, so each key has its own.</summary>
    [Fact]
    public async Task A_key_is_checked_on_demand_at_most_once_a_minute()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;

        Assert.Equal(ValidationOutcome.TooSoon, (await app.Credentials.ValidateAsync(userId, added.Id, Cancellation)).Outcome);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var checks = await Task.WhenAll(
            app.Credentials.ValidateAsync(userId, added.Id, Cancellation),
            app.Credentials.ValidateAsync(userId, added.Id, Cancellation));

        Assert.Equal(
            [ValidationOutcome.Checked, ValidationOutcome.TooSoon],
            checks.Select(check => check.Outcome).Order());
        Assert.Equal(2, app.Ai.WithKey(CredentialApp.AiKey).Count);
    }

    /// <summary>An outage says nothing about the key, so it can't downgrade one, or later pause its agents.</summary>
    [Fact]
    public async Task A_check_that_cannot_reach_the_provider_changes_nothing()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        var stored = await app.Credentials.FindAsync(userId, added.Id, Cancellation);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "later"));
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);

        Assert.Equal(ValidationOutcome.Unreachable, result.Outcome);
        Assert.Equal(stored, result.Credential);
        Assert.Single(await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation));
    }

    [Fact]
    public async Task An_unverified_key_becomes_valid_once_the_provider_answers()
    {
        await using var app = new CredentialApp(postgres);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "later"));
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        app.AiAccepts();
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);

        Assert.Equal((CredentialStatus.Unverified, CredentialStatus.Valid), (added.Status, result.Credential!.Status));
        Assert.NotNull(result.Credential.ValidatedAt);
    }

    /// <summary>FR-KEY-05. Someone can turn withdrawals on after the key was added, and then it's never used.</summary>
    [Fact]
    public async Task A_Binance_key_given_withdrawals_since_it_was_added_becomes_invalid()
    {
        await using var app = new CredentialApp(postgres);
        app.BinanceAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddBinanceAsync(userId)).Credential!;
        app.BinanceAccepts(withdrawals: true);
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);

        Assert.Equal((CredentialStatus.Invalid, (CredentialProblem?)CredentialProblem.WithdrawalsEnabled), (result.Credential!.Status, result.Credential.Problem));
        await Assert.ThrowsAsync<CredentialUnusableException>(
            () => app.Keys.OpenExchangeKeyAsync(userId, added.Id, Kernel.Market.Spot, Cancellation));
    }

    /// <summary>A rotation while the old key is checked has its own result, which the check mustn't overwrite.</summary>
    [Fact]
    public async Task A_check_of_a_key_replaced_meanwhile_leaves_the_new_key_s_status()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var userId = Guid.NewGuid();
        var added = (await app.AddAiAsync(userId)).Credential!;
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, async (request, _) =>
        {
            if (request.Key != CredentialApp.AiKey)
            {
                return FakeAiProvider.Models(null, "model-a");
            }

            Assert.Equal(SaveOutcome.Saved, (await app.RotateAiAsync(userId, added.Id)).Outcome);
            return FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "revoked by the rotation");
        });

        var result = await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);

        Assert.Equal(ValidationOutcome.Checked, result.Outcome);
        Assert.Equal(("ABCD", CredentialStatus.Valid), (result.Credential!.LastFour, result.Credential.Status));
        Assert.DoesNotContain(
            await AuditRows.OfUserAsync(app.ConnectionString, userId, Cancellation), record => record.Action == "CredentialValidated");
    }

    [Fact]
    public async Task A_check_of_someone_else_s_key_finds_nothing_and_sends_nothing()
    {
        await using var app = new CredentialApp(postgres);
        app.AiAccepts();
        var added = (await app.AddAiAsync(Guid.NewGuid())).Credential!;
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var requests = app.Ai.Requests.Count;

        var result = await app.Credentials.ValidateAsync(Guid.NewGuid(), added.Id, Cancellation);

        Assert.Equal(ValidationOutcome.NotFound, result.Outcome);
        Assert.Equal(requests, app.Ai.Requests.Count);
    }
}
