namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// Entry point of the AI module. Clients are stateless singletons: the key handle is passed to every call and never
/// retained, so nothing has to be released when a key is deleted or rotated (FR-KEY-06).
/// </summary>
public interface IAiClientFactory
{
    IAiProviderClient GetClient(AiProvider provider);
}
