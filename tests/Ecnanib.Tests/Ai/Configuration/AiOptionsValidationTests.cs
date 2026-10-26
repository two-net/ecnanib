using Ecnanib.Ai;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ai.Configuration;

public class AiOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValidAndPointAtTheProviders()
    {
        var options = Resolve([]);

        Assert.Equal("https://api.anthropic.com/v1", options.Claude.BaseUrl);
        Assert.Equal("2023-06-01", options.Claude.ApiVersion);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", options.Gemini.BaseUrl);
        Assert.Equal("https://api.openai.com/v1", options.Gpt.BaseUrl);
        Assert.Equal("https://api.x.ai/v1", options.EndpointsFor(AiProvider.Grok).BaseUrl);
        Assert.Equal(TimeSpan.FromSeconds(60), options.CallTimeout);
        Assert.Equal(8192, options.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromHours(6), options.ModelSyncInterval);
        Assert.Equal(3, options.DeprecationMissThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Retry.MaxRetryAfterWait);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new()
        {
            ["Providers:CallTimeoutSeconds"] = "30",
            ["Providers:MaxOutputTokens"] = "1024",
            ["Providers:Grok:BaseUrl"] = "https://eu-west.api.x.ai/v1",
        });

        Assert.Equal(TimeSpan.FromSeconds(30), options.CallTimeout);
        Assert.Equal(1024, options.MaxOutputTokens);
        Assert.Equal("https://eu-west.api.x.ai/v1", options.Grok.BaseUrl);
        Assert.Equal("https://api.openai.com/v1", options.Gpt.BaseUrl);
    }

    [Theory]
    [InlineData("Providers:CallTimeoutSeconds", "0", "CallTimeoutSeconds")]
    [InlineData("Providers:MaxOutputTokens", "0", "MaxOutputTokens")]
    [InlineData("Providers:DeprecationMissThreshold", "0", "DeprecationMissThreshold")]
    [InlineData("Providers:Claude:BaseUrl", "http://api.anthropic.com/v1", "Providers:Claude:BaseUrl must use the https scheme")]
    [InlineData("Providers:Gpt:BaseUrl", "not a url", "Providers:Gpt:BaseUrl must be an absolute URL")]
    [InlineData("Providers:Grok:BaseUrl", "https://grok.example/v1", "Providers:Grok:BaseUrl must be a host under x.ai: outbound traffic is limited to the four providers (NFR-SEC-04)")]
    [InlineData("Providers:Gpt:BaseUrl", "https://api.anthropic.com/v1", "Providers:Gpt:BaseUrl must be a host under openai.com")]
    [InlineData("Providers:Claude:BaseUrl", "https://evilanthropic.com/v1", "Providers:Claude:BaseUrl must be a host under anthropic.com")]
    [InlineData("Providers:Gemini:BaseUrl", "https://googleapis.com.example/v1beta", "Providers:Gemini:BaseUrl must be a host under googleapis.com")]
    [InlineData("Providers:Gemini:BaseUrl", "", "Providers:Gemini:BaseUrl is required")]
    [InlineData("Providers:Claude:ApiVersion", "", "Providers:Claude:ApiVersion is required")]
    [InlineData("Providers:Retry:MaxAttempts", "0", "Providers:Retry:MaxAttempts must be > 0")]
    [InlineData("Providers:Retry:MaxDelayMs", "1", "Providers:Retry:MaxDelayMs must be >= BaseDelayMs")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static AiOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddAiProviders(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AiOptions>>().Value;
    }
}
