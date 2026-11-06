using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Logging;
using static Ecnanib.Tests.Council.ContractSamples;

namespace Ecnanib.Tests.Council;

/// <summary>
/// FR-PRM-08, NFR-MNT-04: every answer is checked against its role's contract (Appendix B), and every way it breaks
/// the contract is reported with its path, without quoting what the model wrote.
/// </summary>
public sealed class OutputContractTests
{
    /// <summary>One change to a role's sample, and the violation it must cause.</summary>
    public static TheoryData<AiRole, string, string?, string, string> Breaks => new()
    {
        // The structure, which the schema checks.
        { AiRole.TechnicalAnalyst, "trend", Quoted("sideways"), "$.trend", "must be one of \"up\", \"down\" or \"range\"" },
        { AiRole.TechnicalAnalyst, "summary", null, "$.summary", "is missing" },
        { AiRole.TechnicalAnalyst, "comment", Quoted("extra"), "$", "has properties that aren't in the contract: it takes exactly trend, htf_trend," },
        { AiRole.TechnicalAnalyst, "signals[0].strength", Quoted("high"), "$.signals[0].strength", "must be a number" },
        { AiRole.TechnicalAnalyst, "setup_present", Quoted("yes"), "$.setup_present", "must be true or false" },
        { AiRole.TechnicalAnalyst, "support_levels", "64000", "$.support_levels", "must be an array" },
        { AiRole.TechnicalAnalyst, "confidence", "1e400", "$.confidence", "must be a number" },
        { AiRole.SentimentAnalyst, "sources[0].published_at", Quoted("yesterday"), "$.sources[0].published_at", "must be an ISO 8601 time with its offset" },
        { AiRole.SentimentAnalyst, "data_freshness", Quoted("2026-11-05T14:00:00"), "$.data_freshness", "or null" },
        { AiRole.SentimentAnalyst, "data_freshness", Quoted("2026-11-05"), "$.data_freshness", "must be an ISO 8601 time" },
        { AiRole.Strategist, "action", Quoted("BUY"), "$.action", "must be one of \"OPEN_LONG\", \"OPEN_SHORT\", \"CLOSE\"," },
        { AiRole.Strategist, "limit_ttl_candles", "2.5", "$.limit_ttl_candles", "must be a whole number or null" },
        { AiRole.Strategist, "side", Quoted("buy"), "$.side", "must be one of \"long\", \"short\" or null" },
        { AiRole.RiskCritic, "modifications", Quoted("none"), "$.modifications", "must be an object or null" },
        { AiRole.RiskCritic, "checklist.funding.result", Quoted("n/a"), "$.checklist.funding.result", "must be one of \"pass\", \"fail\" or \"not_applicable\"" },
        { AiRole.Reviewer, "outcome.pnl", "null", "$.outcome.pnl", "must be a number" },

        // The rules beyond the schema.
        { AiRole.TechnicalAnalyst, "confidence", "1.5", "$.confidence", "must be from 0 to 1" },
        { AiRole.TechnicalAnalyst, "signals[0].strength", "-0.1", "$.signals[0].strength", "must be from 0 to 1" },
        { AiRole.TechnicalAnalyst, "signals[0].name", Quoted(" "), "$.signals[0].name", "can't be blank" },
        { AiRole.TechnicalAnalyst, "support_levels[1]", "0", "$.support_levels[1]", "must be above 0" },
        { AiRole.TechnicalAnalyst, "resistance_levels[0]", "-1", "$.resistance_levels[0]", "must be above 0" },
        { AiRole.TechnicalAnalyst, "summary", Quoted(new string('a', 601)), "$.summary", "must be at most 600 characters" },
        { AiRole.TechnicalAnalyst, "summary", Quoted(""), "$.summary", "can't be blank" },
        { AiRole.SentimentAnalyst, "score", "-1.2", "$.score", "must be from -1 to 1" },
        { AiRole.SentimentAnalyst, "confidence", "2", "$.confidence", "must be from 0 to 1" },
        { AiRole.SentimentAnalyst, "summary", Quoted(new string('a', 601)), "$.summary", "must be at most 600 characters" },
        { AiRole.SentimentAnalyst, "sources[0].url", Quoted("javascript:alert(1)"), "$.sources[0].url", "must be an absolute http or https URL" },
        { AiRole.SentimentAnalyst, "sources[0].url", Quoted("/etc/passwd"), "$.sources[0].url", "must be an absolute http or https URL" },
        { AiRole.SentimentAnalyst, "sources[0].title", Quoted(""), "$.sources[0].title", "can't be blank" },
        { AiRole.Strategist, "side", Quoted("short"), "$.side", "must be \"long\" for `OPEN_LONG`" },
        { AiRole.Strategist, "side", "null", "$.side", "is required for `OPEN_LONG`" },
        { AiRole.Strategist, "entry_type", "null", "$.entry_type", "is required for `OPEN_LONG`" },
        { AiRole.Strategist, "stop_price", "null", "$.stop_price", "is required for `OPEN_LONG`" },
        { AiRole.Strategist, "stop_price", "0", "$.stop_price", "must be above 0" },
        { AiRole.Strategist, "target_prices[0]", "-66000", "$.target_prices[0]", "must be above 0" },
        { AiRole.Strategist, "entry_price", "null", "$.entry_price", "is required for a limit entry" },
        { AiRole.Strategist, "entry_price", "-1", "$.entry_price", "must be above 0" },
        { AiRole.Strategist, "limit_ttl_candles", "null", "$.limit_ttl_candles", "is required for a limit entry" },
        { AiRole.Strategist, "limit_ttl_candles", "0", "$.limit_ttl_candles", "must be at least 1" },
        { AiRole.Strategist, "risk_percent", "null", "$.risk_percent", "is required for `OPEN_LONG`" },
        { AiRole.Strategist, "risk_percent", "0", "$.risk_percent", "must be above 0" },
        { AiRole.Strategist, "risk_percent", "1.01", "$.risk_percent", "must be at most 1, the risk settings' maximum risk per trade" },
        { AiRole.Strategist, "invalidation", "null", "$.invalidation", "is required for `OPEN_LONG`" },
        { AiRole.Strategist, "rationale", Quoted(""), "$.rationale", "can't be blank" },
        { AiRole.Strategist, "confidence", "1.1", "$.confidence", "must be from 0 to 1" },
        { AiRole.RiskCritic, "modifications", "null", "$.modifications", "is required when the verdict is \"modify\"" },
        { AiRole.RiskCritic, "modifications.stop_price", "63000", "$.modifications.stop_price", "must be tighter than the proposal's stop: higher, for a long" },
        { AiRole.RiskCritic, "modifications.risk_percent", "0.8", "$.modifications.risk_percent", "must be lower than the proposal's risk percent" },
        { AiRole.RiskCritic, "modifications.risk_percent", "-0.1", "$.modifications.risk_percent", "must be above 0" },
        { AiRole.RiskCritic, "modifications.target_prices", "[66000,68000]", "$.modifications.target_prices[1]", "must be no farther than the proposal's farthest target" },
        { AiRole.RiskCritic, "rationale", Quoted(" "), "$.rationale", "can't be blank" },
        { AiRole.Reviewer, "lesson", Quoted(""), "$.lesson", "can't be blank" },
        { AiRole.Reviewer, "setup_summary", Quoted(" "), "$.setup_summary", "can't be blank" },
    };

    [Theory]
    [InlineData(AiRole.TechnicalAnalyst)]
    [InlineData(AiRole.SentimentAnalyst)]
    [InlineData(AiRole.Strategist)]
    [InlineData(AiRole.RiskCritic)]
    [InlineData(AiRole.Reviewer)]
    public void Each_roles_sample_conforms(AiRole role)
    {
        var check = OutputContracts.Check(role, For(role), ContextFor(role));

        Assert.Empty(check.Violations);
        Assert.True(check.Conforms);
        Assert.Equal(role, check.Output!.Role);
    }

    [Theory]
    [MemberData(nameof(Breaks))]
    public void Each_break_is_reported_at_its_path(AiRole role, string path, string? value, string expectedPath, string expectedProblem)
    {
        var check = OutputContracts.Check(role, With(For(role), path, value), ContextFor(role));

        Assert.Null(check.Output);
        Assert.False(check.Conforms);
        Assert.Contains(check.Violations, violation => violation.Path == expectedPath && violation.Problem.Contains(expectedProblem, StringComparison.Ordinal));
    }

    [Fact]
    public void Answers_are_read_exactly()
    {
        var technical = Assert.IsType<TechnicalAssessment>(OutputContracts.Check(AiRole.TechnicalAnalyst, Technical, ContextFor(AiRole.TechnicalAnalyst)).Output);
        var sentiment = Assert.IsType<SentimentAssessment>(OutputContracts.Check(AiRole.SentimentAnalyst, Sentiment, ContextFor(AiRole.SentimentAnalyst)).Output);
        var proposal = ProposalOutput();
        var verdict = Assert.IsType<CriticVerdict>(OutputContracts.Check(AiRole.RiskCritic, Verdict, ContextFor(AiRole.RiskCritic)).Output);
        var reflection = Assert.IsType<TradeReflection>(OutputContracts.Check(AiRole.Reviewer, Reflection, ContextFor(AiRole.Reviewer)).Output);

        Assert.Equal((Trend.Up, Trend.Up, VolatilityRegime.Normal, true, 0.65m), (technical.Trend, technical.HtfTrend, technical.VolatilityRegime, technical.SetupPresent, technical.Confidence));
        Assert.Equal(new[] { 64000.5m, 63500m }, technical.SupportLevels);
        Assert.Equal(new[] { 66000.25m }, technical.ResistanceLevels);
        Assert.Equal(new TechnicalSignal("ema20 above ema50", SignalDirection.Bullish, 0.7m), Assert.Single(technical.Signals));

        Assert.Equal(0.35m, sentiment.Score);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero), sentiment.DataFreshness);
        Assert.Equal(new SentimentSource("ETF inflows rise", "https://news.example/etf", new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero)), Assert.Single(sentiment.Sources));

        Assert.Equal(
            (TradeAction.OpenLong, (PositionSide?)PositionSide.Long, (EntryType?)EntryType.Limit, (decimal?)64250.5m, (decimal?)63500m, (decimal?)0.8m, (int?)4, (decimal?)null, 0.6m),
            (proposal.Action, proposal.Side, proposal.EntryType, proposal.EntryPrice, proposal.StopPrice, proposal.RiskPercent, proposal.LimitTtlCandles, proposal.CloseFraction, proposal.Confidence));
        Assert.Equal(new[] { 66000m, 67500.75m }, proposal.TargetPrices);

        Assert.Equal(VerdictKind.Modify, verdict.Verdict);
        Assert.Equal(((decimal?)63800m, (decimal?)0.5m), (verdict.Modifications!.StopPrice, verdict.Modifications.RiskPercent));
        Assert.Equal(ChecklistResult.NotApplicable, verdict.Checklist.Reflections.Result);
        Assert.Equal(ChecklistResult.Fail, verdict.Checklist.Funding.Result);

        Assert.Equal(new TradeOutcome(1.8m, 42.5m), reflection.Outcome);
        Assert.Equal(new[] { "pullback" }, reflection.Tags);
    }

    /// <summary>FR-CYC-03: each market's actions only.</summary>
    [Fact]
    public void A_market_allows_only_its_own_actions()
    {
        var context = ContextFor(AiRole.Strategist, Market.Spot);

        Assert.Contains(
            OutputContracts.Check(AiRole.Strategist, Proposal, context).Violations,
            violation => violation.Path == "$.action" && violation.Problem.Contains("\"BUY\", \"SELL\"", StringComparison.Ordinal));
        Assert.Contains(
            OutputContracts.Check(AiRole.Strategist, With(Proposal, "action", Quoted("OPEN_SHORT")), context).Violations,
            violation => violation.Path == "$.action");

        var buy = OutputContracts.Check(AiRole.Strategist, With(Proposal, "action", Quoted("BUY")), context);
        Assert.Equal(TradeAction.Buy, Assert.IsType<TradeProposal>(buy.Output).Action);

        var shortOnSpot = OutputContracts.Check(AiRole.Strategist, With(With(Proposal, "action", Quoted("BUY")), "side", Quoted("short")), context);
        Assert.Contains(shortOnSpot.Violations, violation => violation.Path == "$.side" && violation.Problem == "must be \"long\" for `BUY`");
    }

    [Fact]
    public void An_entry_can_ask_for_the_maximum_risk_but_no_more()
    {
        Assert.True(OutputContracts.Check(AiRole.Strategist, With(Proposal, "risk_percent", "1"), ContextFor(AiRole.Strategist)).Conforms);
        Assert.True(OutputContracts.Check(AiRole.Strategist, With(Proposal, "risk_percent", "5"), new ContractContext(Market.Futures)).Conforms);
        Assert.False(OutputContracts.Check(AiRole.Strategist, With(Proposal, "risk_percent", "1.0001"), ContextFor(AiRole.Strategist)).Conforms);
    }

    [Fact]
    public void A_market_entry_needs_no_price_or_life_and_keeps_neither()
    {
        var market = With(With(With(Proposal, "entry_type", Quoted("market")), "entry_price", "null"), "limit_ttl_candles", "null");
        var proposal = Assert.IsType<TradeProposal>(OutputContracts.Check(AiRole.Strategist, market, ContextFor(AiRole.Strategist)).Output);

        var stray = With(Proposal, "entry_type", Quoted("market"));
        var cleared = Assert.IsType<TradeProposal>(OutputContracts.Check(AiRole.Strategist, stray, ContextFor(AiRole.Strategist)).Output);

        Assert.Equal(((EntryType?)EntryType.Market, (decimal?)null, (int?)null), (proposal.EntryType, proposal.EntryPrice, proposal.LimitTtlCandles));
        Assert.Equal(((decimal?)null, (int?)null), (cleared.EntryPrice, cleared.LimitTtlCandles));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("1", true)]
    [InlineData("0.25", true)]
    [InlineData("0", false)]
    [InlineData("-0.5", false)]
    [InlineData("1.5", false)]
    public void A_close_takes_all_or_part_of_the_position(string fraction, bool conforms)
    {
        var close = With(With(Proposal, "action", Quoted("CLOSE")), "close_fraction", fraction);

        var check = OutputContracts.Check(AiRole.Strategist, close, ContextFor(AiRole.Strategist));

        Assert.Equal(conforms, check.Conforms);
        if (conforms)
        {
            var proposal = Assert.IsType<TradeProposal>(check.Output);
            Assert.Equal(fraction == "null" ? (decimal?)null : decimal.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture), proposal.CloseFraction);
            Assert.Equal(((PositionSide?)null, (EntryType?)null, (decimal?)null, (decimal?)null, 0), (proposal.Side, proposal.EntryType, proposal.StopPrice, proposal.RiskPercent, proposal.TargetPrices.Count));
        }
        else
        {
            Assert.Equal("$.close_fraction", Assert.Single(check.Violations).Path);
        }
    }

    [Fact]
    public void An_adjustment_needs_only_what_it_moves_and_keeps_only_that()
    {
        var stop = OutputContracts.Check(AiRole.Strategist, With(Proposal, "action", Quoted("ADJUST_STOP")), ContextFor(AiRole.Strategist));
        var noStop = OutputContracts.Check(AiRole.Strategist, With(With(Proposal, "action", Quoted("ADJUST_STOP")), "stop_price", "null"), ContextFor(AiRole.Strategist));
        var target = OutputContracts.Check(AiRole.Strategist, With(With(Proposal, "action", Quoted("ADJUST_TARGET")), "target_prices", "[]"), ContextFor(AiRole.Strategist));

        var moved = Assert.IsType<TradeProposal>(stop.Output);
        Assert.Equal(((decimal?)63500m, (decimal?)null, 0, (PositionSide?)null), (moved.StopPrice, moved.EntryPrice, moved.TargetPrices.Count, moved.Side));
        Assert.Equal(("$.stop_price", "is required for `ADJUST_STOP`"), (Assert.Single(noStop.Violations).Path, noStop.Violations[0].Problem));

        var removed = Assert.IsType<TradeProposal>(target.Output);
        Assert.Equal((0, (decimal?)null), (removed.TargetPrices.Count, removed.StopPrice));
    }

    /// <summary>A field a hold doesn't use is noise, which isn't worth a retry, and is cleared so nothing reads it.</summary>
    [Fact]
    public void A_hold_ignores_and_clears_the_fields_it_doesnt_use()
    {
        var hold = With(With(With(Proposal, "action", Quoted("HOLD")), "side", Quoted("short")), "stop_price", "-5");

        var proposal = Assert.IsType<TradeProposal>(OutputContracts.Check(AiRole.Strategist, hold, ContextFor(AiRole.Strategist)).Output);

        Assert.Equal(
            (TradeAction.Hold, (PositionSide?)null, (EntryType?)null, (decimal?)null, (decimal?)null, 0, (decimal?)null, (int?)null, (decimal?)null),
            (proposal.Action, proposal.Side, proposal.EntryType, proposal.EntryPrice, proposal.StopPrice, proposal.TargetPrices.Count, proposal.RiskPercent, proposal.LimitTtlCandles, proposal.CloseFraction));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public void Only_a_modify_verdict_has_modifications(string verdict)
    {
        var withChanges = With(Verdict, "verdict", Quoted(verdict));
        var without = With(withChanges, "modifications", "null");

        Assert.Equal(
            ("$.modifications", $"must be null when the verdict is \"{verdict}\""),
            (Assert.Single(OutputContracts.Check(AiRole.RiskCritic, withChanges, ContextFor(AiRole.RiskCritic)).Violations).Path,
                OutputContracts.Check(AiRole.RiskCritic, withChanges, ContextFor(AiRole.RiskCritic)).Violations[0].Problem));
        Assert.True(OutputContracts.Check(AiRole.RiskCritic, without, ContextFor(AiRole.RiskCritic)).Conforms);
    }

    [Fact]
    public void A_modification_changes_something()
    {
        var nothing = With(Verdict, "modifications", """{"stop_price":null,"target_prices":[],"risk_percent":null}""");

        var violation = Assert.Single(OutputContracts.Check(AiRole.RiskCritic, nothing, ContextFor(AiRole.RiskCritic)).Violations);

        Assert.Equal(("$.modifications", "must change at least one of stop_price, target_prices and risk_percent"), (violation.Path, violation.Problem));
    }

    /// <summary>Appendix B: the Risk Critic's changes only reduce risk.</summary>
    [Fact]
    public void A_modification_only_reduces_risk_for_the_proposals_side()
    {
        var shortProposal = ProposalOutput() with { Action = TradeAction.OpenShort, Side = PositionSide.Short, StopPrice = 65000m, TargetPrices = [63000m, 62000m] };
        var shortContext = new ContractContext(Market.Futures) { Proposal = shortProposal };

        Assert.True(Check(With(Verdict, "modifications.stop_price", "64500"), shortContext).Conforms);
        Assert.Equal("must be tighter than the proposal's stop: lower, for a short", Single(With(Verdict, "modifications.stop_price", "65500"), shortContext).Problem);
        Assert.Equal("must be tighter than the proposal's stop: lower, for a short", Single(With(Verdict, "modifications.stop_price", "65000"), shortContext).Problem);
        Assert.True(Check(With(Verdict, "modifications.target_prices", "[63500,62000]"), shortContext).Conforms);
        Assert.Equal("$.modifications.target_prices[0]", Single(With(With(Verdict, "modifications.stop_price", "64500"), "modifications.target_prices", "[61000]"), shortContext).Path);

        // Equal isn't tighter, and a target exactly at the farthest isn't farther.
        Assert.False(Check(With(Verdict, "modifications.stop_price", "63500"), ContextFor(AiRole.RiskCritic)).Conforms);
        Assert.True(Check(With(Verdict, "modifications.target_prices", "[67500.75]"), ContextFor(AiRole.RiskCritic)).Conforms);
        Assert.False(Check(With(Verdict, "modifications.risk_percent", "0.8"), ContextFor(AiRole.RiskCritic)).Conforms);
    }

    [Fact]
    public void A_modification_of_an_adjustment_takes_the_side_from_the_position()
    {
        var adjust = ProposalOutput() with { Action = TradeAction.AdjustStop, Side = null, EntryType = null, RiskPercent = null, TargetPrices = [] };
        var onShort = new ContractContext(Market.Futures) { Proposal = adjust, PositionSide = PositionSide.Short };
        var noRisk = With(Verdict, "modifications.risk_percent", "null");

        Assert.True(Check(With(noRisk, "modifications.stop_price", "63000"), onShort).Conforms);
        Assert.Equal("$.modifications.stop_price", Single(noRisk, onShort).Path);
        Assert.Equal(
            ("$.modifications.risk_percent", "must be null, because the proposal asks for no risk percent"),
            (Single(With(Verdict, "modifications.stop_price", "63000"), onShort).Path, Single(With(Verdict, "modifications.stop_price", "63000"), onShort).Problem));

        // Adding a target to a proposal that has none takes nothing away.
        Assert.True(Check(With(With(noRisk, "modifications.stop_price", "null"), "modifications.target_prices", "[61000]"), onShort).Conforms);
    }

    [Fact]
    public void A_modification_to_a_stop_the_proposal_lacks_is_refused()
    {
        var cancel = ProposalOutput() with { Action = TradeAction.CancelPending, StopPrice = null, RiskPercent = null };
        var context = new ContractContext(Market.Futures) { Proposal = cancel };

        Assert.Contains(
            Check(Verdict, context).Violations,
            violation => violation is { Path: "$.modifications.stop_price", Problem: "must be null, because the proposal has no stop to tighten" });
    }

    [Fact]
    public void Blank_items_in_lists_of_text_are_dropped()
    {
        var reflection = Assert.IsType<TradeReflection>(
            OutputContracts.Check(AiRole.Reviewer, With(With(Reflection, "tags", """["pullback"," ",""]"""), "what_failed", """[""]"""), ContextFor(AiRole.Reviewer)).Output);
        var sentiment = Assert.IsType<SentimentAssessment>(
            OutputContracts.Check(AiRole.SentimentAnalyst, With(Sentiment, "drivers", """["","ETF inflows"]"""), ContextFor(AiRole.SentimentAnalyst)).Output);

        Assert.Equal(new[] { "pullback" }, reflection.Tags);
        Assert.Empty(reflection.WhatFailed);
        Assert.Equal(new[] { "ETF inflows" }, sentiment.Drivers);
    }

    [Theory]
    [InlineData("```json\n{0}\n```")]
    [InlineData("```JSON\n{0}\n```")]
    [InlineData("```\n{0}\n```")]
    [InlineData("  \n{0}\n\n")]
    public void Whitespace_and_one_code_fence_around_the_answer_are_allowed(string wrapping)
    {
        var answer = wrapping.Replace("{0}", Technical, StringComparison.Ordinal);

        Assert.True(OutputContracts.Check(AiRole.TechnicalAnalyst, answer, ContextFor(AiRole.TechnicalAnalyst)).Conforms);
    }

    [Theory]
    [InlineData("Here is my assessment: {0}")]
    [InlineData("{0}\nI hope this helps.")]
    [InlineData("```python\n{0}\n```")]
    [InlineData("Sure.\n```json\n{0}\n```")]
    [InlineData("{0}{0}")]
    public void Anything_else_around_the_answer_is_a_violation(string wrapping)
    {
        var answer = wrapping.Replace("{0}", Technical, StringComparison.Ordinal);

        var violation = Assert.Single(OutputContracts.Check(AiRole.TechnicalAnalyst, answer, ContextFor(AiRole.TechnicalAnalyst)).Violations);

        Assert.Equal("$", violation.Path);
        Assert.StartsWith("isn't one JSON object and nothing else: it stops being valid JSON at line ", violation.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("  \n ", "is empty")]
    [InlineData("[]", "must be an object")]
    [InlineData("null", "must be an object")]
    public void An_answer_that_isnt_an_object_is_a_violation(string answer, string problem)
    {
        var violation = Assert.Single(OutputContracts.Check(AiRole.Reviewer, answer, ContextFor(AiRole.Reviewer)).Violations);

        Assert.Equal(("$", problem), (violation.Path, violation.Problem));
    }

    [Fact]
    public void A_property_given_twice_is_a_violation()
    {
        var twice = Technical.TrimEnd()[..^1] + ""","confidence":0.9}""";

        var violation = Assert.Single(OutputContracts.Check(AiRole.TechnicalAnalyst, twice, ContextFor(AiRole.TechnicalAnalyst)).Violations);

        Assert.Equal(("$.confidence", "appears more than once"), (violation.Path, violation.Problem));
    }

    [Fact]
    public void A_truncated_answer_is_a_violation_even_when_it_parses()
    {
        var violation = Assert.Single(OutputContracts.Check(AiRole.TechnicalAnalyst, Technical, ContextFor(AiRole.TechnicalAnalyst), truncated: true).Violations);

        Assert.Equal(("$", "was cut off at the output limit, so it's incomplete: answer more briefly"), (violation.Path, violation.Problem));
    }

    [Fact]
    public void Violations_are_capped()
    {
        var levels = "[" + string.Join(',', Enumerable.Repeat("0", 30)) + "]";

        var check = OutputContracts.Check(AiRole.TechnicalAnalyst, With(Technical, "support_levels", levels), ContextFor(AiRole.TechnicalAnalyst));

        Assert.Equal(OutputContracts.MaxViolations, check.Violations.Count);
        Assert.Equal("$.support_levels[0]", check.Violations[0].Path);
    }

    /// <summary>
    /// A violation goes back to the model and into the log, so it never quotes the answer, which a model or a web
    /// page it read could have filled with instructions or a secret.
    /// </summary>
    [Fact]
    public void A_violation_never_quotes_the_answer()
    {
        var canary = CanaryCases.NewCanary();
        var answers = new[]
        {
            With(With(Technical, "trend", Quoted(canary)), canary, Quoted(canary)),
            With(Technical, "summary", Quoted(canary + new string('a', 600))),
            With(Technical, "signals[0].strength", Quoted(canary)),
            With(Sentiment, "sources[0].url", Quoted(canary)),
            With(Sentiment, "data_freshness", Quoted(canary)),
            Technical + canary,
            canary,
            $$"""{"{{canary}}":1,"{{canary}}":2}""",
        };

        foreach (var answer in answers)
        {
            var role = answer.Contains("score", StringComparison.Ordinal) ? AiRole.SentimentAnalyst : AiRole.TechnicalAnalyst;
            var violations = OutputContracts.Check(role, answer, ContextFor(role)).Violations;

            Assert.NotEmpty(violations);
            Assert.All(violations, violation => Assert.DoesNotContain(canary, violation.Path + violation.Problem, StringComparison.Ordinal));
        }
    }

    /// <summary>FR-LOG-07: an output logged by mistake shows nothing a model wrote.</summary>
    [Fact]
    public void Nothing_shows_what_the_model_wrote()
    {
        var canary = CanaryCases.NewCanary();
        var outputs = new object?[]
        {
            OutputContracts.Check(AiRole.TechnicalAnalyst, With(With(Technical, "summary", Quoted(canary)), "signals[0].name", Quoted(canary)), ContextFor(AiRole.TechnicalAnalyst)).Output,
            OutputContracts.Check(AiRole.SentimentAnalyst, With(With(Sentiment, "summary", Quoted(canary)), "sources[0].title", Quoted(canary)), ContextFor(AiRole.SentimentAnalyst)).Output,
            OutputContracts.Check(AiRole.Strategist, With(Proposal, "rationale", Quoted(canary)), ContextFor(AiRole.Strategist)).Output,
            OutputContracts.Check(AiRole.RiskCritic, With(With(Verdict, "rationale", Quoted(canary)), "checklist.funding.note", Quoted(canary)), ContextFor(AiRole.RiskCritic)).Output,
            OutputContracts.Check(AiRole.Reviewer, With(With(Reflection, "lesson", Quoted(canary)), "tags", $"[{Quoted(canary)}]"), ContextFor(AiRole.Reviewer)).Output,
        };

        Assert.All(outputs, Assert.NotNull);

        var rendered = string.Join('\n', outputs.Select(output => output!.ToString()))
            + ((TechnicalAssessment)outputs[0]!).Signals[0]
            + ((SentimentAssessment)outputs[1]!).Sources[0]
            + ((CriticVerdict)outputs[3]!).Checklist.Funding;

        Assert.DoesNotContain(canary, rendered, StringComparison.Ordinal);
        Assert.Contains("TradeProposal OpenLong Long Limit, 2 targets", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_context_comes_from_the_prompts_request()
    {
        var request = new PromptRequest(AiRole.RiskCritic, Market.Spot, Timeframe.OneHour, new PromptContext
        {
            Risk = RiskSettings.Default with { MaxRiskPerTradePercent = 1.5m },
            Position = new PromptPosition(PositionSide.Long, 0.1m, 64000m, DateTimeOffset.UnixEpoch),
        });
        var proposal = ProposalOutput();

        var context = ContractContext.For(request, proposal);

        Assert.Equal((Market.Spot, (decimal?)1.5m, (PositionSide?)PositionSide.Long, (TradeProposal?)proposal), (context.Market, context.MaxRiskPercent, context.PositionSide, context.Proposal));
        var empty = ContractContext.For(new PromptRequest(AiRole.Strategist, Market.Futures, Timeframe.OneDay, PromptContext.Empty));
        Assert.Equal(
            (Market.Futures, (decimal?)null, (PositionSide?)null, (TradeProposal?)null),
            (empty.Market, empty.MaxRiskPercent, empty.PositionSide, empty.Proposal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContractContext((Market)99));
    }

    private static ContractCheck Check(string answer, ContractContext context) => OutputContracts.Check(AiRole.RiskCritic, answer, context);

    private static ContractViolation Single(string answer, ContractContext context) => Assert.Single(Check(answer, context).Violations);
}
