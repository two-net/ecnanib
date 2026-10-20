using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Council.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Council.Services;

/// <summary>Appendix C validation of a role's answer (FR-AI-07): shape, enums, ranges, the fields an action needs and the text bounds.</summary>
public class RoleOutputParserTests
{
    private static readonly CouncilOptions Options = new();

    [Fact]
    public void ParsesARoleOutput()
    {
        var document = RoleOutputParser.Parse(CouncilTestHost.RoleJson("open_long", "limit", 100m, 96m, [108m, 112m], 0.65m, "Solid.", ["setup_valid", " Setup_Valid ", "extended"]), final: false, Options, out var rejection);

        Assert.Null(rejection);
        var output = Assert.IsType<RoleOutputDocument>(document, exactMatch: true);
        Assert.Equal("Solid.", output.Assessment);
        Assert.Equal(0.65m, output.Confidence);
        Assert.Equal(DecisionAction.OpenLong, output.ProposedAction);
        Assert.Equal(OrderType.Limit, output.EntryType);
        Assert.Equal(100m, output.EntryPrice);
        Assert.Equal(96m, output.StopPrice);
        Assert.Equal([108m, 112m], output.Targets);
        Assert.Equal(["setup_valid", "extended"], output.Flags);
    }

    [Fact]
    public void ParsesAFinalDecision()
    {
        var document = RoleOutputParser.Parse(CouncilTestHost.FinalJson("modify", stop: 97m, risk: null, rationale: "Lock in.", partial: 0.5m), final: true, Options, out var rejection);

        Assert.Null(rejection);
        var final = Assert.IsType<FinalDecisionDocument>(document);
        Assert.Equal(DecisionAction.Modify, final.ProposedAction);
        Assert.Null(final.EntryType);
        Assert.Equal(97m, final.StopPrice);
        Assert.Equal("Lock in.", final.Rationale);
        Assert.Equal(0.5m, final.PartialFraction);
        Assert.Null(final.RiskPercent);
    }

    [Theory]
    [InlineData("{ nope", "not valid JSON")]
    [InlineData("[1,2]", "one JSON object")]
    [InlineData("""{"confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "'assessment' must be a string")]
    [InlineData("""{"assessment":"  ","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "'assessment' must not be empty")]
    [InlineData("""{"assessment":"x","confidence":1.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "between 0 and 1")]
    [InlineData("""{"assessment":"x","confidence":"high","proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "'confidence' must be a number")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"buy","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "'proposed_action' must be one of")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"stop","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "'entry_type' must be one of")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":-1,"stop_price":null,"targets":[],"flags":[]}""", "'entry_price' must be greater than 0")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[1,"two"],"flags":[]}""", "every 'targets' entry must be a number")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":"none"}""", "'flags' must be an array")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"open_long","entry_type":"limit","entry_price":100,"stop_price":null,"targets":[],"flags":[]}""", "an entry needs a 'stop_price'")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"open_long","entry_type":"none","entry_price":100,"stop_price":95,"targets":[],"flags":[]}""", "an entry needs 'entry_type'")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"open_long","entry_type":"limit","entry_price":null,"stop_price":95,"targets":[],"flags":[]}""", "a limit entry needs an 'entry_price'")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"modify","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", "a modify needs a 'stop_price', 'targets'")]
    public void RejectsABadRoleOutput(string text, string fragment)
    {
        var document = RoleOutputParser.Parse(text, final: false, Options, out var rejection);

        Assert.Null(document);
        Assert.Contains(fragment, rejection);
    }

    [Theory]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[],"risk_percent":null,"take_partial_fraction":null}""", "'rationale' must be a string")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[],"risk_percent":0,"rationale":"r","take_partial_fraction":null}""", "'risk_percent' must be greater than 0")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"hold","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[],"risk_percent":null,"rationale":"r","take_partial_fraction":1.5}""", "'take_partial_fraction' must be greater than 0 and at most 1")]
    [InlineData("""{"assessment":"x","confidence":0.5,"proposed_action":"modify","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[],"risk_percent":null,"rationale":"r","take_partial_fraction":null}""", "or 'take_partial_fraction'")]
    public void RejectsABadFinalDecision(string text, string fragment)
    {
        var document = RoleOutputParser.Parse(text, final: true, Options, out var rejection);

        Assert.Null(document);
        Assert.Contains(fragment, rejection);
    }

    [Fact]
    public void AMarketEntryDropsThePriceAndANonEntryDropsTheType()
    {
        var market = RoleOutputParser.Parse(CouncilTestHost.RoleJson("open_short", "market", 100m, 104m), final: false, Options, out _)!;
        Assert.Equal(OrderType.Market, market.EntryType);
        Assert.Null(market.EntryPrice);

        var hold = RoleOutputParser.Parse(CouncilTestHost.RoleJson("hold", "limit", 100m, 95m), final: false, Options, out _)!;
        Assert.Null(hold.EntryType);
        Assert.Equal(100m, hold.EntryPrice);
    }

    [Fact]
    public void ANullEntryTypeReadsAsNone()
    {
        var document = RoleOutputParser.Parse("""{"assessment":"x","confidence":0.5,"proposed_action":"no_trade","entry_type":null,"entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", final: false, Options, out var rejection);

        Assert.Null(rejection);
        Assert.Null(document!.EntryType);
    }

    [Fact]
    public void LongTextIsCutAndListsAreCapped()
    {
        var options = new CouncilOptions { MaxAssessmentLength = 100, MaxRationaleLength = 50 };
        var text = CouncilTestHost.FinalJson(assessment: new string('a', 500), rationale: new string('r', 500), targets: [1m, 2m, 3m, 4m, 5m, 6m, 7m],
            flags: Enumerable.Range(0, 20).Select(i => "flag" + i + new string('x', 100)).ToArray());

        var document = Assert.IsType<FinalDecisionDocument>(RoleOutputParser.Parse(text, final: true, options, out var rejection));

        Assert.Null(rejection);
        Assert.Equal(100, document.Assessment.Length);
        Assert.Equal(50, document.Rationale.Length);
        Assert.Equal(RoleOutputParser.MaxTargets, document.Targets.Count);
        Assert.Equal(RoleOutputParser.MaxFlags, document.Flags.Count);
        Assert.All(document.Flags, f => Assert.Equal(RoleOutputParser.MaxFlagLength, f.Length));
    }

    [Fact]
    public void SeveralProblemsAreReportedTogether()
    {
        RoleOutputParser.Parse("""{"assessment":"","confidence":2,"proposed_action":"x","entry_type":"none","entry_price":null,"stop_price":null,"targets":[],"flags":[]}""", final: false, Options, out var rejection);

        Assert.Contains("'assessment' must not be empty; 'confidence' must be between 0 and 1; 'proposed_action' must be one of", rejection);
    }
}
