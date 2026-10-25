using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Logging.Services;

public class LogReaderTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100\\%")]
    [InlineData("agent_id", "agent\\_id")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("%_\\", "\\%\\_\\\\")]
    public void SearchTermsAreMadeLiteralInsideThePattern(string term, string expected) => Assert.Equal(expected, LogReader.EscapeLike(term));

    [Fact]
    public async Task ADownDatabaseIsAnExceptionLikeEverywhereElse()
    {
        var factory = new ThrowingDbContextFactory();
        var reader = new LogReader(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.QueryAsync(Guid.NewGuid(), new LogQuery(Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public void TheQueryDefaultsAreTheDocumentedOnes()
    {
        var query = new LogQuery(Guid.NewGuid());

        Assert.Equal((1, 50, (Microsoft.Extensions.Logging.LogLevel?)null, (string?)null, (string?)null), (query.Page, query.PageSize, query.MinLevel, query.Component, query.Search));
        Assert.Equal(200, LogQuery.MaxPageSize);
    }
}
