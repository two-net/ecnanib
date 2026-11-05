using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using static Ecnanib.Tests.Prompts.PromptHarness;

namespace Ecnanib.Tests.Prompts;

/// <summary>FR-PRM-01 and FR-PRM-02: layers compose in order, and the most specific template of each layer wins.</summary>
public sealed class PromptCompositionTests
{
    [Fact]
    public void The_instructions_hold_the_stored_layers_in_order()
    {
        var prompt = Compose(Request(AiRole.RiskCritic, Market.Spot, Timeframe.OneDay));

        Assert.Equal(
            "SYSTEM\n\nCONTRACT RiskCritic\n\nROLE RiskCritic\n\nMARKET Spot\n\nTIMEFRAME OneDay",
            prompt.Instructions);
        Assert.Equal(PromptComposer.Layers, prompt.Templates.Select(template => template.Key.Layer));
        Assert.Equal(
            [PromptLayer.System, PromptLayer.OutputContract, PromptLayer.Role, PromptLayer.Market, PromptLayer.Timeframe],
            PromptComposer.Layers);
    }

    [Fact]
    public void The_message_holds_the_custom_instructions_and_then_the_runtime_context()
    {
        var prompt = Compose(Request(context: new PromptContext { Snapshot = Snapshot() }) with { CustomInstructions = "Avoid weekends." });

        Assert.StartsWith("<custom_instructions>\nAvoid weekends.\n</custom_instructions>\n\n<runtime_context>\n", prompt.Message, StringComparison.Ordinal);
        Assert.EndsWith("</runtime_context>", prompt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("weekends", prompt.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_context_still_gives_a_message()
    {
        Assert.Equal("<runtime_context>\n</runtime_context>", Compose(Request()).Message);
    }

    /// <summary>
    /// Overrides of the 15m overlay: for every Futures role, and for the Strategist on Futures. Each combination
    /// takes the most specific one that matches it, and an override never reaches a combination it doesn't name.
    /// </summary>
    [Theory]
    [InlineData(AiRole.Strategist, Market.Futures, Timeframe.FifteenMinutes, "STRATEGIST FUTURES 15m")]
    [InlineData(AiRole.RiskCritic, Market.Futures, Timeframe.FifteenMinutes, "FUTURES 15m")]
    [InlineData(AiRole.Strategist, Market.Spot, Timeframe.FifteenMinutes, "TIMEFRAME FifteenMinutes")]
    [InlineData(AiRole.Strategist, Market.Futures, Timeframe.OneHour, "TIMEFRAME OneHour")]
    public void The_most_specific_template_wins(AiRole role, Market market, Timeframe timeframe, string overlay)
    {
        var templates = Generic();
        templates.Add(Template(new PromptTemplateKey(PromptLayer.Timeframe, null, Market.Futures, Timeframe.FifteenMinutes), "FUTURES 15m"));
        templates.Add(Template(
            new PromptTemplateKey(PromptLayer.Timeframe, AiRole.Strategist, Market.Futures, Timeframe.FifteenMinutes),
            "STRATEGIST FUTURES 15m"));

        var prompt = PromptComposer.Compose(templates, Request(role, market, timeframe));

        Assert.EndsWith("\n\n" + overlay, prompt.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void More_dimensions_win_and_between_as_many_role_beats_market_which_beats_timeframe()
    {
        Wins(new(PromptLayer.System, null, Market.Futures, Timeframe.FifteenMinutes), new(PromptLayer.System, AiRole.Strategist));
        Wins(new(PromptLayer.Market, AiRole.Strategist, Market.Futures), new(PromptLayer.Market, null, Market.Futures, Timeframe.FifteenMinutes));
        Wins(
            new(PromptLayer.Timeframe, AiRole.Strategist, null, Timeframe.FifteenMinutes),
            new(PromptLayer.Timeframe, null, Market.Futures, Timeframe.FifteenMinutes));
        Wins(new(PromptLayer.System, AiRole.Strategist), new(PromptLayer.System, Market: Market.Futures));
        Wins(new(PromptLayer.System, Market: Market.Futures), new(PromptLayer.System, Timeframe: Timeframe.FifteenMinutes));

        // Whichever comes first, for Strategist, Futures, 15m, which both keys match.
        static void Wins(PromptTemplateKey winner, PromptTemplateKey loser)
        {
            foreach (var order in new[] { new[] { winner, loser }, [loser, winner] })
            {
                var templates = Generic();
                templates.AddRange(order.Select(key => Template(key, key == winner ? "WINNER" : "LOSER")));

                var used = PromptComposer.Resolve(templates, winner.Layer, AiRole.Strategist, Market.Futures, Timeframe.FifteenMinutes);

                Assert.Equal("WINNER", used.Content);
            }
        }
    }

    [Fact]
    public void The_highest_version_of_a_key_is_used_and_reported()
    {
        var key = new PromptTemplateKey(PromptLayer.Market, Market: Market.Futures);
        var templates = Generic().Where(template => template.Key != key).ToList();
        templates.Add(Template(key, "V1", 1));
        templates.Add(Template(key, "V3", 3));
        templates.Add(Template(key, "V2", 2));

        var prompt = PromptComposer.Compose(templates, Request());

        Assert.Contains("\n\nV3\n\n", prompt.Instructions, StringComparison.Ordinal);
        var market = Assert.Single(prompt.Templates, template => template.Key.Layer == PromptLayer.Market);
        Assert.Equal(3, market.Version);
        Assert.Equal(templates.Single(template => template.Content == "V3").Id, market.Id);
    }

    [Fact]
    public void A_layer_with_no_template_for_the_combination_stops_composition_and_is_named()
    {
        var templates = Generic().Where(template => template.Key.Market != Market.Spot).ToList();

        var exception = Assert.Throws<PromptTemplateMissingException>(
            () => PromptComposer.Compose(templates, Request(AiRole.Reviewer, Market.Spot, Timeframe.FourHours)));

        Assert.Equal(PromptLayer.Market, exception.Layer);
        Assert.Equal("No Market template matches Reviewer, Spot, FourHours.", exception.Message);
        PromptComposer.Compose(templates, Request(AiRole.Reviewer, Market.Futures, Timeframe.FourHours));
    }

    [Fact]
    public void Two_templates_with_the_same_key_and_version_are_refused()
    {
        var templates = Generic();
        templates.Add(Template(new PromptTemplateKey(PromptLayer.System), "AGAIN"));

        Assert.Throws<ArgumentException>(() => PromptComposer.Compose(templates, Request()));
    }

    [Fact]
    public void A_snapshot_for_another_market_or_timeframe_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Compose(Request(context: new PromptContext { Snapshot = Snapshot(Market.Spot) })));
        Assert.Throws<ArgumentException>(
            () => Compose(Request(context: new PromptContext { Snapshot = Snapshot(timeframe: Timeframe.OneHour) })));
    }

    [Theory]
    [InlineData(PromptLayer.Role, null, null, null)]
    [InlineData(PromptLayer.OutputContract, null, Market.Spot, Timeframe.OneDay)]
    [InlineData(PromptLayer.Market, AiRole.Strategist, null, Timeframe.OneDay)]
    [InlineData(PromptLayer.Timeframe, AiRole.Strategist, Market.Spot, null)]
    public void A_key_without_its_layers_own_dimension_is_refused(PromptLayer layer, AiRole? role, Market? market, Timeframe? timeframe)
    {
        var exception = Assert.Throws<ArgumentException>(() => new PromptTemplateKey(layer, role, market, timeframe));

        Assert.Contains($"A {layer} template needs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_with_a_value_that_isnt_defined_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptTemplateKey((PromptLayer)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptTemplateKey(PromptLayer.System, (AiRole)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptTemplateKey(PromptLayer.System, Market: (Market)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptTemplateKey(PromptLayer.System, Timeframe: (Timeframe)99));
    }

    [Fact]
    public void A_template_needs_a_version_from_1_and_some_text_within_the_limit()
    {
        var key = new PromptTemplateKey(PromptLayer.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => Template(key, "TEXT", 0));
        Assert.Throws<ArgumentException>(() => Template(key, " \n"));
        Assert.Throws<ArgumentException>(() => Template(key, new string('x', PromptLimits.TemplateMaxLength + 1)));
        Template(key, new string('x', PromptLimits.TemplateMaxLength));
    }
}
