using Ecnanib.Ai.Abstractions;
using Ecnanib.Components.Providers;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Tests.ModelSync.Pages;

public class ProviderMessagesTests
{
    [Theory]
    [InlineData(1, 0, "GPT was synced: 1 model listed.")]
    [InlineData(12, 1, "GPT was synced: 12 models listed, 1 model newly deprecated.")]
    [InlineData(0, 2, "GPT was synced: 0 models listed, 2 models newly deprecated.")]
    public void SyncedNamesTheCounts(int listed, int deprecated, string expected) =>
        Assert.Equal(expected, ProviderMessages.Synced(AiProvider.Gpt, listed, deprecated));

    [Theory]
    [InlineData(null, "The Gemini sync failed. The catalog is unchanged; try again.")]
    [InlineData("  ", "The Gemini sync failed. The catalog is unchanged; try again.")]
    [InlineData("503 Service Unavailable.", "The Gemini sync failed: 503 Service Unavailable. The catalog is unchanged; try again.")]
    public void FailedCarriesTheDetailWithOnePeriod(string? error, string expected) =>
        Assert.Equal(expected, ProviderMessages.Failed(AiProvider.Gemini, error));

    [Fact]
    public void NoKeyPointsToTheKeysPage() =>
        Assert.Equal("You have no valid Grok key, so you cannot sync its catalog. Add one on the keys page.", ProviderMessages.NoKey(AiProvider.Grok));

    [Fact]
    public void TriggersAndStatusesRenderAsPlainWordsAndPillClasses()
    {
        Assert.Equal("", ProviderMessages.Trigger(null));
        Assert.Equal("key validation", ProviderMessages.Trigger(SyncTrigger.KeyValidation));
        Assert.Equal("scheduled", ProviderMessages.Trigger(SyncTrigger.Scheduled));
        Assert.Equal("manual", ProviderMessages.Trigger(SyncTrigger.Manual));
        Assert.Equal("status-never", ProviderMessages.StatusClass(SyncStatus.Never));
        Assert.Equal("status-failed", ProviderMessages.StatusClass(SyncStatus.Failed));
        Assert.Equal("status-deprecated", ProviderMessages.StatusClass(ModelStatus.Deprecated));
    }

    /// <summary>The Sync now button posts the route name, which the handler parses back (the only way a provider reaches the service).</summary>
    [Fact]
    public void EveryProviderRoundTripsThroughItsRouteName()
    {
        foreach (var provider in Enum.GetValues<AiProvider>())
            Assert.Equal(provider, ProviderNames.Parse(ProviderNames.Route(provider)));
        Assert.Null(ProviderNames.Parse("foo"));
    }
}
