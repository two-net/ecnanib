using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Providers;

internal sealed class AiClientFactory : IAiClientFactory
{
    private readonly ClaudeClient _claude;
    private readonly GeminiClient _gemini;
    private readonly GptClient _gpt;
    private readonly GrokClient _grok;

    public AiClientFactory(ClaudeClient claude, GeminiClient gemini, GptClient gpt, GrokClient grok)
    {
        _claude = claude;
        _gemini = gemini;
        _gpt = gpt;
        _grok = grok;
    }

    public IAiProviderClient GetClient(AiProvider provider) => provider switch
    {
        AiProvider.Claude => _claude,
        AiProvider.Gemini => _gemini,
        AiProvider.Gpt => _gpt,
        AiProvider.Grok => _grok,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider."),
    };
}
