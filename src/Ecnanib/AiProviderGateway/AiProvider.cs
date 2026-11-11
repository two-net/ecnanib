using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// An AI provider the application works with, as a row of the <c>ai_providers</c> table, so that
/// a credential or a catalog model can point at its provider. The rows are seeded through
/// migrations (NFR-DAT-10).
/// </summary>
public sealed class AiProvider(AiProviderCode code, string displayName)
{
    public AiProviderCode Code { get; } = code;

    /// <summary>The name shown to users, such as "Claude".</summary>
    public string DisplayName { get; } = displayName;
}

internal sealed class AiProviderConfiguration : IEntityTypeConfiguration<AiProvider>
{
    public void Configure(EntityTypeBuilder<AiProvider> builder)
    {
        builder.ToTable("ai_providers");
        builder.HasKey(provider => provider.Code);
        builder.Property(provider => provider.DisplayName);

        // The same codes name the entries under AiProviders in the tracked configuration file.
        builder.HasData(
            new AiProvider(new AiProviderCode("claude"), "Claude"),
            new AiProvider(new AiProviderCode("gemini"), "Gemini"),
            new AiProvider(new AiProviderCode("gpt"), "GPT"),
            new AiProvider(new AiProviderCode("grok"), "Grok"));
    }
}
