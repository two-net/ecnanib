using Ecnanib.Components.Catalog;
using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Tests.Catalog;

/// <summary>An issue that adds a status, capability, trigger or problem has to give it text, or these fail.</summary>
public sealed class CatalogTextTests
{
    [Fact]
    public void Every_value_the_pages_show_has_text_of_its_own()
    {
        IReadOnlyList<IReadOnlyList<string>> groups =
        [
            [.. Enum.GetValues<ModelStatus>().Select(CatalogText.StatusOf)],
            [.. Enum.GetValues<ModelCapability>().Select(CatalogText.CapabilityOf)],
            [.. Enum.GetValues<CatalogSyncTrigger>().Select(CatalogText.TriggerOf)],
            [.. Enum.GetValues<SyncAllOutcome>().Select(CatalogText.SyncAllOf)],
            [.. Enum.GetValues<ModelEditField>().Select(CatalogText.FieldErrorOf)],
            [.. Enum.GetValues<RoleDefaultProblem>().Select(CatalogText.RoleDefaultProblemOf)],
            [CatalogText.FlagOf(true), CatalogText.FlagOf(false), CatalogText.FlagOf(null)],
        ];

        foreach (var texts in groups)
        {
            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(texts.Count, texts.Distinct().Count());
        }
    }

    /// <summary>The four launch providers have their product's name, and any other shows as it is.</summary>
    [Theory]
    [InlineData("Google", "Google Gemini")]
    [InlineData("xAI", "xAI Grok")]
    [InlineData("Test", "Test")]
    public void A_provider_shows_as_its_product(string provider, string text) =>
        Assert.Equal(text, CatalogText.ProviderOf(provider));
}
