using System.Net.Http;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Persistence;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Catalog;

/// <summary>FR-CAT-01 and FR-CAT-06: what providers report about a model, and what the configuration adds.</summary>
public sealed class CatalogMetadataTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    /// <summary>
    /// Through the real adapters, from bodies shaped like each provider's. A fine-tuned model names another
    /// user's organisation, so it never reaches the catalog that every user sees (NFR-SEC-04).
    /// </summary>
    [Fact]
    public async Task What_providers_say_about_their_models_reaches_the_catalog()
    {
        await using var app = await SyncedAsync();

        var models = (await app.Catalog.ListAsync(Cancellation)).ToDictionary(model => model.ModelId);
        var claude = models["claude-opus-5"];

        Assert.Equal(("Anthropic", "Claude claude-opus-5", "opus"), (claude.Provider, claude.DisplayName, claude.Family));
        Assert.Equal((1_000_000, 128_000), (claude.ContextWindow, claude.MaxOutputTokens));
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero), claude.ReleasedAt);
        Assert.Equal(new ModelCapabilities(ImageInput: true, StructuredOutput: true, ToolUse: null, LiveSearch: false, ReasoningEffort: true), claude.Capabilities);
        Assert.Null(claude.Price);
        Assert.Equal(new ModelCapabilities(null, null, null, null, ReasoningEffort: true), models["gemini-3-pro"].Capabilities);
        Assert.Equal(new ModelCapabilities(ImageInput: true, null, null, null, null), models["grok-4.7"].Capabilities);
        Assert.Equal(("gpt-6-luna", null), (models["gpt-6-luna"].DisplayName, models["gpt-6-luna"].Family));
        Assert.DoesNotContain(models.Keys, id => id.StartsWith("ft:", StringComparison.Ordinal));
    }

    /// <summary>
    /// ASM-04: pricing and some capabilities need maintaining by hand. The configuration beats the provider,
    /// field by field, and describes only models a provider listed.
    /// </summary>
    [Fact]
    public async Task The_configuration_fills_in_what_providers_leave_out_and_beats_what_they_say()
    {
        await using var app = await SyncedAsync(new()
        {
            ["Catalog:Overrides:0:Provider"] = "Anthropic",
            ["Catalog:Overrides:0:Model"] = "claude-opus-5",
            ["Catalog:Overrides:0:DisplayName"] = "Claude Opus 5",
            ["Catalog:Overrides:0:Family"] = "Claude Opus",
            ["Catalog:Overrides:0:InputPricePerMillion"] = "5",
            ["Catalog:Overrides:0:OutputPricePerMillion"] = "25",
            ["Catalog:Overrides:0:LiveSearch"] = "true",
            ["Catalog:Overrides:0:ToolUse"] = "true",
            ["Catalog:Overrides:1:Provider"] = "OpenAI",
            ["Catalog:Overrides:1:Model"] = "gpt-6-luna",
            ["Catalog:Overrides:1:InputPricePerMillion"] = "1.25",
            ["Catalog:Overrides:1:OutputPricePerMillion"] = "10.000001",
            ["Catalog:Overrides:1:StructuredOutput"] = "true",
            ["Catalog:Overrides:1:ContextWindow"] = "400000",
            ["Catalog:Overrides:2:Provider"] = "xAI",
            ["Catalog:Overrides:2:Model"] = "grok-unlisted",
            ["Catalog:Overrides:2:InputPricePerMillion"] = "1",
            ["Catalog:Overrides:2:OutputPricePerMillion"] = "2",
        });

        var models = (await app.Catalog.ListAsync(Cancellation)).ToDictionary(model => model.ModelId);
        var claude = models["claude-opus-5"];
        var gpt = models["gpt-6-luna"];

        Assert.Equal(("Claude Opus 5", "Claude Opus", new ModelPrice(5m, 25m)), (claude.DisplayName, claude.Family, claude.Price));
        Assert.Equal(new ModelCapabilities(ImageInput: true, StructuredOutput: true, ToolUse: true, LiveSearch: true, ReasoningEffort: true), claude.Capabilities);
        Assert.Equal((new ModelPrice(1.25m, 10.000001m), 400_000, true), (gpt.Price, gpt.ContextWindow, gpt.Capabilities.StructuredOutput));
        Assert.DoesNotContain("grok-unlisted", models.Keys);
    }

    /// <summary>One user's key for each of the four providers, synced by the schedule.</summary>
    private async Task<CatalogApp> SyncedAsync(Dictionary<string, string?>? settings = null)
    {
        var app = new CatalogApp(postgres, settings, observers: false);
        app.Ai.On(HttpMethod.Get, "/v1/models", request => request.Host switch
        {
            AnthropicHost => Json(Anthropic.Models(null, "claude-opus-5")),
            OpenAiHost => Json(OpenAi.Models("gpt-6-luna", "ft:gpt-6-luna:acme-trading::Ab12Cd34")),
            _ => FakeAiProvider.Models(null, "model-a"),
        });
        app.Ai.On(HttpMethod.Get, "/v1beta/models", _ => Json(Google.Models(null, "gemini-3-pro")));
        app.Ai.On(HttpMethod.Get, "/v1/language-models", _ => Json(Xai.Models(("grok-4.7", "text"))));

        foreach (var provider in new[] { "Anthropic", "Google", "OpenAI", "xAI" })
        {
            await app.AddAsync(Guid.NewGuid(), provider: provider);
        }

        await app.RunScheduleAsync();

        return app;
    }
}
