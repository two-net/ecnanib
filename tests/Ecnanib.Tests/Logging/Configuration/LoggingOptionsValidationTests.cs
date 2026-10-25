using Ecnanib.Logging;
using Ecnanib.Logging.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Logging.Configuration;

public class LoggingOptionsValidationTests
{
    private static readonly Dictionary<string, string?> Reference = new()
    {
        ["Logging:Console:MinimumLevel"] = "Information",
        ["Logging:Database:MinimumLevel"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:Binance"] = "Warning",
        ["Logging:LogLevel:CryptoExchange"] = "Warning",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
        ["Logging:LogLevel:Npgsql"] = "Warning",
    };

    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal(LogLevel.Information, options.Console.Minimum);
        Assert.Equal(LogLevel.Information, options.Database.Minimum);
        Assert.Empty(options.LogLevel);
    }

    [Fact]
    public void TheReferenceShapeIsValidAndOverridesAreHonoured()
    {
        var values = new Dictionary<string, string?>(Reference) { ["Logging:Console:MinimumLevel"] = "debug", ["Logging:LogLevel:Ecnanib.Council"] = "Trace" };

        var options = Resolve(values);

        Assert.Equal(LogLevel.Debug, options.Console.Minimum);
        Assert.Equal(LogLevel.Information, options.Database.Minimum);
        Assert.Equal("Warning", options.LogLevel["binance"]);
        Assert.Equal("Trace", options.LogLevel["Ecnanib.Council"]);
    }

    [Theory]
    [InlineData("Logging:Console:MinimumLevel", "Verbose", "Logging:Console:MinimumLevel must be Trace, Debug, Information, Warning, Error or Critical.")]
    [InlineData("Logging:Database:MinimumLevel", "None", "Logging:Database:MinimumLevel must be Trace, Debug, Information, Warning, Error or Critical.")]
    [InlineData("Logging:Database:MinimumLevel", "2", "Logging:Database:MinimumLevel must be Trace")]
    [InlineData("Logging:LogLevel:Default", "Information", "Logging:LogLevel:Default is not used; set Logging:Console:MinimumLevel and Logging:Database:MinimumLevel instead.")]
    [InlineData("Logging:Console:LogLevel:Default", "Warning", "Logging:Console:LogLevel is not supported; the levels in Logging:LogLevel apply to every sink.")]
    [InlineData("Logging:Database:LogLevel:Microsoft", "Warning", "Logging:Database:LogLevel is not supported")]
    [InlineData("Logging:LogLevel:Microsoft.*", "Warning", "Logging:LogLevel:Microsoft.* must be a category prefix without wildcards.")]
    [InlineData("Logging:LogLevel:Npgsql", "loud", "Logging:LogLevel:Npgsql must be Trace, Debug, Information, Warning, Error or Critical.")]
    [InlineData("Logging:LogLevel:Binance", "Debug", "Logging:LogLevel:Binance must be Information or higher: the exchange library's Debug output carries API keys and signatures (FR-LOG-05).")]
    [InlineData("Logging:LogLevel:Binance.RestClient", "Trace", "Logging:LogLevel:Binance.RestClient must be Information or higher")]
    [InlineData("Logging:LogLevel:CryptoExchange", "Debug", "Logging:LogLevel:CryptoExchange must be Information or higher")]
    [InlineData("Logging:Console:MinimumLevel", "Debug", "Logging:LogLevel:Binance must be set to Information or higher while Logging:Console:MinimumLevel is Debug")]
    [InlineData("Logging:Database:MinimumLevel", "Trace", "Logging:LogLevel:CryptoExchange must be set to Information or higher while Logging:Database:MinimumLevel is Trace")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void ADebugSinkIsFineWhenTheExchangeCategoriesAreCapped()
    {
        var values = new Dictionary<string, string?>(Reference) { ["Logging:Console:MinimumLevel"] = "Trace", ["Logging:Database:MinimumLevel"] = "Debug", ["Logging:LogLevel:CryptoExchange"] = "Information" };

        var options = Resolve(values);

        Assert.Equal(LogLevel.Trace, options.Console.Minimum);
    }

    private static LoggingOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<LoggingOptions>>().Value;
    }
}
