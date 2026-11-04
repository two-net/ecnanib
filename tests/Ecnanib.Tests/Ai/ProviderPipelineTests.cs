using System.Net;
using System.Reflection;
using Ecnanib.Modules.AiAdapters;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Ecnanib.Tests.Ai.ProviderSamples;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// CON-13, EXT-AI-01, EXT-AI-05: the four real adapters, through the real pipeline, to their configured
/// servers. The fake stands in for every server, and answers in each provider's own format.
/// </summary>
public sealed class ProviderPipelineTests(PostgresFixture postgres)
{
    public static TheoryData<string> Providers => ["Anthropic", "Google", "OpenAI", "xAI"];

    [Fact]
    public void The_four_providers_are_registered_in_order()
    {
        Assert.Equal(["Anthropic", "Google", "OpenAI", "xAI"], Adapters.All.Select(adapter => adapter.Name));
    }

    /// <summary>The section appsettings.json ships passes the startup checks as Production, https included (CFG-03, NFR-SEC-10).</summary>
    [Fact]
    public void The_shipped_configuration_names_each_providers_official_server()
    {
        using var services = Adapters.Shipped();
        var configuration = services.GetRequiredService<IConfiguration>();

        Assert.Equal(
            new[] { "Anthropic", "Google", "OpenAI", "xAI" }.Select(name => new AiProviderInfo(name, true)),
            services.GetRequiredService<IAiProviders>().All);
        Assert.Equal(
            ["https://api.anthropic.com", "https://generativelanguage.googleapis.com", "https://api.openai.com", "https://api.x.ai"],
            new[] { "Anthropic", "Google", "OpenAI", "xAI" }.Select(name => configuration[$"AiProviders:{name}:BaseUrl"]));
    }

    /// <summary>An adapter takes nothing from the container, so a key can only arrive with a call, never from the configuration.</summary>
    [Fact]
    public void No_adapter_can_read_a_key_from_the_configuration()
    {
        var adapters = typeof(AiAdaptersModule).Assembly.GetTypes()
            .Where(type => typeof(IAiProviderAdapter).IsAssignableFrom(type) && type is { IsInterface: false, IsAbstract: false })
            .ToList();

        Assert.Equal(4, adapters.Count);
        Assert.All(
            adapters.SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)),
            constructor => Assert.Empty(constructor.GetParameters()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Every_page_of_models_comes_from_the_configured_server_with_the_key_in_its_header(string provider)
    {
        await using var ai = new AiHarness(postgres);
        var wire = Wire.For(provider);
        ai.Provider.OnSequence(HttpMethod.Get, wire.ModelsPath, [.. wire.ModelPages.Select(page => (Func<RecordedAiRequest, HttpResponseMessage>)(_ => Json(page)))]);
        var key = Key(provider);

        var models = await ai.Providers.ListModelsAsync(key, AiHarness.Cancellation);

        Assert.Equal(wire.Models, models.Select(model => model.Id));
        Assert.Equal(wire.ModelPages.Length, ai.Provider.Requests.Count);
        Assert.All(ai.Provider.Requests, request => wire.AssertSent(request));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_completion_comes_back_with_its_usage(string provider)
    {
        await using var ai = new AiHarness(postgres);
        var wire = Wire.For(provider);
        ai.Provider.On(HttpMethod.Post, wire.CompletePath, _ => Json(wire.Answer));
        var key = Key(provider);

        var response = await ai.Completions.CompleteAsync(key, Request(schema: true), AiHarness.Cancellation);

        Assert.Equal("""{"trend":"up","confidence":0.7}""", response.Text);
        Assert.Equal(wire.Usage, response.Usage);
        Assert.Equal(1, response.Attempts);
        wire.AssertSent(Assert.Single(ai.Provider.Requests));
    }

    /// <summary>FR-KEY-04: Credentials validates a key by listing models, and each provider says "wrong key" its own way.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_wrong_key_is_an_authentication_failure_and_isnt_retried(string provider)
    {
        await using var ai = new AiHarness(postgres);
        var wire = Wire.For(provider);
        ai.Provider.On(HttpMethod.Get, wire.ModelsPath, _ => Json(wire.WrongKey, wire.WrongKeyStatus));
        var key = Key(provider);

        var exception = await Assert.ThrowsAsync<AiException>(() => ai.Providers.ListModelsAsync(key, AiHarness.Cancellation));

        Assert.Equal((provider, AiFailure.Authentication, 1), (exception.Provider, exception.Failure, exception.Attempts));
        Assert.Equal((int)wire.WrongKeyStatus, exception.StatusCode);
    }

    /// <summary>How one provider looks on the wire.</summary>
    private sealed record Wire(
        string Host,
        string KeyHeader,
        string KeyValue,
        string ModelsPath,
        string[] ModelPages,
        string[] Models,
        string CompletePath,
        string Answer,
        AiUsage Usage,
        HttpStatusCode WrongKeyStatus,
        string WrongKey)
    {
        public static Wire For(string provider) => provider switch
        {
            "Anthropic" => new(
                AnthropicHost,
                "x-api-key",
                AiHarness.ApiKey,
                "/v1/models",
                [Anthropic.Models("claude-sonnet-5", "claude-opus-5", "claude-sonnet-5"), Anthropic.Models(null, "claude-haiku-4-5")],
                ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5"],
                "/v1/messages",
                Anthropic.Answer(),
                new AiUsage(120, 40),
                HttpStatusCode.Unauthorized,
                Anthropic.Error("authentication_error", "invalid x-api-key")),
            "Google" => new(
                GoogleHost,
                "x-goog-api-key",
                AiHarness.ApiKey,
                "/v1beta/models",
                [Google.Models("page-2", "gemini-3-pro"), Google.Models(null, "gemini-2.5-flash")],
                ["gemini-3-pro", "gemini-2.5-flash"],
                $"/v1beta/models/{Model}:generateContent",
                Google.Answer(),
                new AiUsage(400, 100),
                HttpStatusCode.BadRequest,
                Google.InvalidKey),
            "OpenAI" => new(
                OpenAiHost,
                "authorization",
                "Bearer " + AiHarness.ApiKey,
                "/v1/models",
                [OpenAi.Models("gpt-6.1-terra", "whisper-1")],
                ["gpt-6.1-terra"],
                "/v1/responses",
                Responses.Answer(),
                new AiUsage(1200, 900),
                HttpStatusCode.Unauthorized,
                OpenAi.InvalidKey),
            "xAI" => new(
                XaiHost,
                "authorization",
                "Bearer " + AiHarness.ApiKey,
                "/v1/language-models",
                [Xai.Models(("grok-4.7", "text"))],
                ["grok-4.7"],
                "/v1/responses",
                Responses.Answer(),
                new AiUsage(1200, 1600),
                HttpStatusCode.BadRequest,
                Xai.InvalidKey),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        public void AssertSent(RecordedAiRequest request)
        {
            Assert.Equal(("https", Host), (request.Uri.Scheme, request.Host));
            Assert.Equal(KeyValue, request.Headers[KeyHeader]);
            Assert.DoesNotContain(AiHarness.ApiKey, request.Uri.ToString(), StringComparison.Ordinal);
        }
    }
}
