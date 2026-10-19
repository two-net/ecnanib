using Ecnanib.Ai.Abstractions;
using Ecnanib.Keys.Configuration;
using Ecnanib.Keys.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Keys.Services;

public class ProviderKeyValidatorTests
{
    private const string ApiKey = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private static readonly AiCredentials Credentials = new(ApiKey);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsModelsUnderAScratchIdForTheRightProvider()
    {
        var ai = new FakeAiClientFactory();
        var validator = Build(ai);

        var result = await validator.ValidateAsync(AiProvider.Gemini, Credentials, Ct);

        Assert.Equal(ValidationVerdict.Valid, result.Verdict);
        Assert.Equal(2, result.Models!.Count);
        Assert.All(result.Models, m => Assert.Equal(AiProvider.Gemini, m.Provider));
        var listing = Assert.Single(ai.Listings);
        Assert.Equal(AiProvider.Gemini, listing.Provider);
        Assert.StartsWith(ExchangeKeyValidator.ScratchPrefix, listing.KeyId);
        Assert.DoesNotContain(ApiKey, listing.ToString());
    }

    [Fact]
    public async Task AnAuthFailureIsInvalidWithTheProvidersReason()
    {
        var ai = new FakeAiClientFactory { OnListModels = (_, _) => throw new AiAuthException("Claude list models failed: invalid x-api-key (authentication_error)", AiProvider.Claude, 401, "authentication_error") };
        var validator = Build(ai);

        var result = await validator.ValidateAsync(AiProvider.Claude, Credentials, Ct);

        Assert.Equal(ValidationVerdict.Invalid, result.Verdict);
        Assert.Contains("authentication_error", result.Reason);
        Assert.Null(result.Models);
    }

    public static TheoryData<Exception> NonAuthFailures => new()
    {
        new AiUnavailableException("Grok list models failed: bad gateway (HTTP 502)", AiProvider.Grok, AiErrorKind.ServerError, 502),
        new AiRateLimitedException("Grok list models failed: rate limited (HTTP 429)", AiProvider.Grok, TimeSpan.FromSeconds(5), null, 429),
        new AiRequestException("Grok list models failed: unreadable answer", AiProvider.Grok, AiErrorKind.InvalidResponse),
    };

    [Theory]
    [MemberData(nameof(NonAuthFailures))]
    public async Task OtherProviderFailuresAreFailedNotInvalid(Exception failure)
    {
        var ai = new FakeAiClientFactory { OnListModels = (_, _) => throw failure };
        var validator = Build(ai);

        var result = await validator.ValidateAsync(AiProvider.Grok, Credentials, Ct);

        Assert.Equal(ValidationVerdict.Failed, result.Verdict);
        Assert.Equal(failure.Message, result.Reason);
    }

    [Fact]
    public async Task AHangingListingTimesOutIntoFailed()
    {
        var time = new FakeTimeProvider();
        var ai = new FakeAiClientFactory { OnListModels = async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return []; } };
        var validator = Build(ai, time, new KeysOptions { ValidationTimeoutSeconds = 45 });

        var pending = validator.ValidateAsync(AiProvider.Gpt, Credentials, Ct);
        await Wait.UntilAsync(() => ai.Listings.Count == 1, "listing started");
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(45));
        var result = await pending;

        Assert.Equal(ValidationVerdict.Failed, result.Verdict);
        Assert.Equal("GPT did not answer within 45 seconds.", result.Reason);
    }

    private static ProviderKeyValidator Build(FakeAiClientFactory ai, TimeProvider? time = null, KeysOptions? options = null) =>
        new(ai, Options.Create(options ?? new KeysOptions()), time ?? TimeProvider.System);
}
