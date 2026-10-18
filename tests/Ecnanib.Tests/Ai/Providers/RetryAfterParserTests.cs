using Ecnanib.Ai.Providers;

namespace Ecnanib.Tests.Ai.Providers;

public class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HeaderDeltaSecondsAndHttpDatesAreRead()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), Parse("7"));
        Assert.Equal(TimeSpan.FromSeconds(7), Parse(Now.AddSeconds(7).ToString("r")));
        Assert.Equal(TimeSpan.Zero, Parse(Now.AddMinutes(-1).ToString("r")));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Parse("0.5"));
        Assert.Null(Parse("garbage"));
        Assert.Null(Parse(null));
        Assert.Null(RetryAfterParser.FromHeaders(null, Now));
    }

    [Theory]
    [InlineData("33s", 33_000)]
    [InlineData("0.5s", 500)]
    [InlineData(" 2s ", 2_000)]
    [InlineData("5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DurationsAreRead(string? value, int? expectedMs)
    {
        Assert.Equal(expectedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, RetryAfterParser.FromDuration(value));
    }

    [Theory]
    [InlineData("Rate limit reached. Please try again in 20s. Visit docs.", 20_000)]
    [InlineData("try again in 6m0s", 360_000)]
    [InlineData("Try Again In 250ms", 250)]
    [InlineData("try again in 1.5s", 1_500)]
    [InlineData("no hint here", null)]
    [InlineData("", null)]
    public void OpenAiMessagesAreRead(string message, int? expectedMs)
    {
        Assert.Equal(expectedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, RetryAfterParser.FromMessage(message));
    }

    private static TimeSpan? Parse(string? retryAfter)
    {
        using var response = new HttpResponseMessage();
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return RetryAfterParser.FromHeaders(response.Headers, Now);
    }
}
