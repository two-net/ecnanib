using Ecnanib.Catalog;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// Price per token can be supplied in the configuration file for models whose provider does not
/// report pricing, so cost can be estimated (FR-CAT-11). A model that the file gives no price
/// has none, which is unknown, and not nothing.
/// </summary>
/// <remarks>
/// The file of the tests gives one price, to the models of Claude that <c>claude-priced-*</c>
/// names, and none to any other provider. A test that needs other prices writes them into its
/// own file.
/// </remarks>
public class ModelPriceTests
{
    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 9, 20, 0, TimeSpan.Zero);

    private static readonly AiProviderCode Claude = new("claude");
    private static readonly AiProviderCode Gemini = new("gemini");

    [Fact]
    public async Task A_model_has_the_price_that_the_file_gives_it_and_no_other_model_has_one()
    {
        using var application = await new CatalogApplication(new TestClock(Start)).StartedAsync();

        // The price of the file, for a model that its pattern names.
        Assert.Equal(new ModelPrice(InputPerMillionTokens: 3m, OutputPerMillionTokens: 15.5m), application.Prices.Of(Claude, "claude-priced-1"));

        // Letter case is ignored, as it is for every pattern of the file.
        Assert.Equal(new ModelPrice(3m, 15.5m), application.Prices.Of(Claude, "Claude-Priced-1"));

        // A model that no entry names has no price: nothing is put in its place.
        Assert.Null(application.Prices.Of(Claude, "claude-test-model"));

        // The price is of the provider's model. A model of the same name elsewhere has none.
        Assert.Null(application.Prices.Of(Gemini, "claude-priced-1"));

        // And neither has a model of a provider that the file has no entry for.
        Assert.Null(application.Prices.Of(new AiProviderCode("llama"), "claude-priced-1"));
    }

    [Fact]
    public async Task The_first_entry_that_names_a_model_is_its_price()
    {
        using var application = await new CatalogApplication(
            new TestClock(Start),
            settings: file => file.WithJson(
                "AiProviders:Claude:Prices",
                """
                [
                  { "Model": "claude-opus-*", "InputPerMillionTokens": 15, "OutputPerMillionTokens": 75 },
                  { "Model": "claude-*", "InputPerMillionTokens": 3, "OutputPerMillionTokens": 15 },
                  { "Model": "claude-haiku-*", "InputPerMillionTokens": 1, "OutputPerMillionTokens": 5 }
                ]
                """)).StartedAsync();

        // The pattern that names fewer models is before the one that names more, and is reached.
        Assert.Equal(new ModelPrice(15m, 75m), application.Prices.Of(Claude, "claude-opus-5-5"));
        Assert.Equal(new ModelPrice(3m, 15m), application.Prices.Of(Claude, "claude-sonnet-5-5"));

        // One that is after a pattern which names its models too is never reached.
        Assert.Equal(new ModelPrice(3m, 15m), application.Prices.Of(Claude, "claude-haiku-5-5"));
    }

    [Fact]
    public async Task A_price_of_nothing_is_a_price()
    {
        using var application = await new CatalogApplication(
            new TestClock(Start),
            settings: file => file.WithJson(
                "AiProviders:Gemini:Prices",
                """[ { "Model": "gemini-free-*", "InputPerMillionTokens": 0, "OutputPerMillionTokens": 0 } ]""")).StartedAsync();

        // A model that is not charged for costs nothing, which is known: it is no missing price.
        var free = application.Prices.Of(Gemini, "gemini-free-1");

        Assert.Equal(new ModelPrice(0m, 0m), free);
        Assert.Equal(0m, free!.CostOf(inputTokens: 1_200_000, outputTokens: 150_000));
    }

    [Theory]

    // A million tokens of each cost what the price says.
    [InlineData(3, 15, 1_000_000, 1_000_000, "18")]

    // Fewer cost their share of it, to the last digit: nothing is rounded.
    [InlineData(3, 15, 1200, 150, "0.00585")]
    [InlineData(0.075, 0.3, 1, 1, "0.000000375")]

    // The tokens of prompts and of answers are charged apart.
    [InlineData(3, 15, 2_000_000, 0, "6")]
    [InlineData(3, 15, 0, 2_000_000, "30")]

    // More tokens than a call ever has, as the calls of a long time add up to.
    [InlineData(15, 75, 9_000_000_000_000, 9_000_000_000_000, "810000000")]
    public void What_tokens_cost_is_their_share_of_the_price_of_a_million(
        double input, double output, long inputTokens, long outputTokens, string cost)
    {
        var price = new ModelPrice((decimal)input, (decimal)output);

        Assert.Equal(decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), price.CostOf(inputTokens, outputTokens));
    }
}
