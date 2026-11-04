using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Tests.Ai;

/// <summary>
/// FR-LOG-07, FR-KEY-09: the contract types never show a prompt, an answer or a key, so logging one by
/// mistake leaks nothing. They also check themselves as they're built, and cost is exact (DATA-R-02).
/// </summary>
public sealed class AiContractTests
{
    private const string Prompt = "Zprompt5f3c";
    private const string Instructions = "Zinstructions9a1b";
    private const string Answer = "Zanswer7d2e";
    private const string ProviderMessage = "Zmessage4c8f";

    [Fact]
    public void Nothing_shows_a_prompt_an_answer_a_key_or_a_providers_message()
    {
        using var secret = SecretValue.FromString("Zkey1a2b3c");
        var request = new AiRequest("test-model", AiRole.RiskCritic, Instructions, [AiMessage.User(Prompt), AiMessage.Assistant(Answer), AiMessage.User(Prompt)], 100);
        var rendered = string.Join(
            '\n',
            request,
            request.Messages[0],
            request.Messages[0].Content[0],
            new AiImage("image/png", new byte[] { 1, 2, 3 }),
            new AiResponse(Answer, new AiUsage(1, 2), 0.1m, false, 1, TimeSpan.FromSeconds(1)),
            new AiCompletion(Answer, AiUsage.Zero),
            new AiProviderError(AiFailure.InvalidRequest, "invalid_request_error", ProviderMessage),
            new AiKey("Test", Guid.NewGuid(), secret));

        Assert.Contains("test-model for RiskCritic, 3 messages", rendered, StringComparison.Ordinal);
        Assert.All(new[] { Prompt, Instructions, Answer, ProviderMessage, "Zkey1a2b3c" }, canary => Assert.DoesNotContain(canary, rendered, StringComparison.Ordinal));
    }

    [Fact]
    public void A_request_checks_itself()
    {
        Assert.Throws<ArgumentException>(() => new AiRequest(" ", AiRole.Strategist, "", [AiMessage.User(Prompt)], 100));
        Assert.Throws<ArgumentException>(() => new AiRequest("m", AiRole.Strategist, "", [], 100));
        Assert.Throws<ArgumentException>(() => new AiRequest("m", AiRole.Strategist, "", [AiMessage.Assistant(Answer)], 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiRequest("m", AiRole.Strategist, "", [AiMessage.User(Prompt)], 0));
        Assert.Throws<ArgumentException>(() => new AiMessage(AiSpeaker.User, []));
        Assert.Throws<ArgumentException>(() => new AiMessage(AiSpeaker.User, [null!]));
        Assert.Throws<ArgumentException>(() => new AiMessage(AiSpeaker.Assistant, [new AiText(Answer), new AiImage("image/png", new byte[] { 1 })]));
        Assert.Throws<ArgumentException>(() => new AiImage("image/svg+xml", new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => new AiImage("image/webp", new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => new AiImage("image/gif", new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => new AiImage("image/png", ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => AiOutputSchema.Parse("has spaces", "{}"));
        Assert.Throws<ArgumentException>(() => AiOutputSchema.Parse("verdict", "[]"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiModelPrice(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiUsage(0, -1));
        Assert.Throws<ArgumentException>(() => new AiModel(""));
    }

    [Fact]
    public void A_schema_outlives_the_document_it_was_read_from()
    {
        var schema = AiOutputSchema.Parse("risk_verdict", """{"type":"object","required":["verdict"]}""");

        Assert.Equal("object", schema.Schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, schema.Schema.GetProperty("required").ValueKind);
    }

    [Fact]
    public void Cost_is_exact_decimal_arithmetic()
    {
        var price = new AiModelPrice(InputPerMillion: 3m, OutputPerMillion: 15m);

        Assert.Equal(0.012207m, price.CostOf(new AiUsage(1234, 567)));
        Assert.Equal(new AiUsage(1235, 569), new AiUsage(1234, 567) + new AiUsage(1, 2));
    }
}
