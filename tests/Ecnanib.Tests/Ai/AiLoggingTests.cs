using System.Net;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Ai;

/// <summary>FR-LOG-07, FR-KEY-09.</summary>
public sealed class AiLoggingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Each_attempt_is_logged_with_its_provider_model_role_latency_tokens_cost_and_outcome_and_never_its_content_or_key()
    {
        var cancellation = TestContext.Current.CancellationToken;

        // Shorter than 64 characters and with no marker, so no masking rule can hide a leak.
        var apiKey = CanaryCases.NewCanary();
        var instructions = CanaryCases.NewCanary();
        var prompt = CanaryCases.NewCanary();
        var answer = CanaryCases.NewCanary();
        var providerMessage = CanaryCases.NewCanary();
        var agentId = Guid.NewGuid();
        var provider = new FakeAiProvider();
        provider.On(HttpMethod.Post, "/v1/complete", request => request.Key == apiKey
            ? FakeAiProvider.Completion(answer, 1200, 300)
            : FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", providerMessage));
        provider.On(HttpMethod.Get, "/v1/models", _ => FakeAiProvider.Models(null, "alpha"));

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            AiHarness.Settings(),
            configureServices: services => AiHarness.Use(services, provider, new TestAdapter("Test"), new TestAdapter("Second")));
        var completions = app.App.Services.GetRequiredService<IAiCompletions>();
        var request = new AiRequest("test-model", AiRole.RiskCritic, instructions, [AiMessage.User(prompt)], 500) { Price = new AiModelPrice(3m, 15m) };

        using (app.Logger("Ecnanib.Tests").BeginScope(new Dictionary<string, object?> { ["AgentId"] = agentId }))
        {
            using var key = SecretValue.FromString(apiKey);
            await completions.CompleteAsync(new AiKey("Test", Guid.NewGuid(), key), request, cancellation);

            using var wrongKey = SecretValue.FromString("wrong-key");
            await Assert.ThrowsAsync<AiException>(() => completions.CompleteAsync(new AiKey("Test", Guid.NewGuid(), wrongKey), request, cancellation));
            await app.App.Services.GetRequiredService<IAiProviders>().ListModelsAsync(new AiKey("Test", Guid.NewGuid(), key), cancellation);
        }

        await app.StopAsync();

        var events = app.FileEvents().Where(entry => Property(entry, "SourceContext") == "AiCouncil").ToList();
        Assert.Equal(3, events.Count);

        var success = Assert.Single(events, entry => Property(entry, "Outcome") == "Success" && Property(entry, "Operation") == "Complete");
        Assert.Equal(
            ("Information", "Test", "test-model", "RiskCritic", "1", "200", "1200", "300", "0.0081", agentId.ToString()),
            (success.GetProperty("Level").GetString(), Property(success, "Provider"), Property(success, "Model"), Property(success, "Role"),
                Property(success, "Attempt"), Property(success, "StatusCode"), Property(success, "InputTokens"), Property(success, "OutputTokens"),
                Property(success, "Cost"), Property(success, "AgentId")));
        Assert.True(success.GetProperty("Properties").GetProperty("LatencyMs").GetDouble() >= 0);

        var failure = Assert.Single(events, entry => Property(entry, "Outcome") == "Authentication");
        Assert.Equal(
            ("Warning", "401", "authentication_error", "0", "0"),
            (failure.GetProperty("Level").GetString(), Property(failure, "StatusCode"), Property(failure, "ErrorCode"),
                Property(failure, "InputTokens"), Property(failure, "Cost")));

        var list = Assert.Single(events, entry => Property(entry, "Operation") == "ListModels");
        Assert.Equal(("Success", null, null), (Property(list, "Outcome"), Property(list, "Model"), Property(list, "Role")));

        var everything = app.FileText() + await app.TableTextAsync(cancellation);
        Assert.All(
            new[] { apiKey, instructions, prompt, answer, providerMessage },
            canary => Assert.DoesNotContain(canary, everything, StringComparison.Ordinal));
    }

    private static string? Property(JsonElement entry, string name) =>
        entry.GetProperty("Properties").TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => null,
                _ => value.GetRawText(),
            }
            : null;
}
