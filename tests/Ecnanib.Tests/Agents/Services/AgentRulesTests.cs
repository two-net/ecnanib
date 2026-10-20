using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Agents.Services;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>Input rules of FR-AGT-01/02/12/13/14 and FR-AI-03/05 that never need the database.</summary>
public class AgentRulesTests
{
    private static readonly RiskCapsOptions Caps = new();

    [Fact]
    public void NormalisesAValidPaperSpotAgent()
    {
        var errors = new List<FieldError>();

        var settings = AgentRules.NormalizeSettings("  BTC  ", MarketType.Spot, " btcusdt ", CandleInterval.OneHour, AgentMode.Paper,
            Guid.NewGuid(), 5, MarginType.Cross, false, Caps, errors);

        Assert.Empty(errors);
        Assert.NotNull(settings);
        Assert.Equal("BTC", settings.Name);
        Assert.Equal("BTCUSDT", settings.Symbol);
        Assert.Null(settings.ExchangeKeyId);
        Assert.Null(settings.Leverage);
        Assert.Null(settings.MarginType);
    }

    [Fact]
    public void FuturesDefaultToIsolatedMarginAndNeedLeverageWithinTheCap()
    {
        var errors = new List<FieldError>();
        var settings = AgentRules.NormalizeSettings("ETH", MarketType.UsdFutures, "ETHUSDT", CandleInterval.FourHours, AgentMode.Paper, null, 3, null, true, Caps, errors);
        Assert.Empty(errors);
        Assert.Equal(MarginType.Isolated, settings!.MarginType);
        Assert.Equal(3, settings.Leverage);
        Assert.True(settings.Discretionary);

        errors.Clear();
        Assert.Null(AgentRules.NormalizeSettings("ETH", MarketType.UsdFutures, "ETHUSDT", CandleInterval.FourHours, AgentMode.Paper, null, 21, null, false, Caps, errors));
        Assert.Equal("leverage", Assert.Single(errors).Field);
    }

    [Theory]
    [InlineData("", "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, "name")]
    [InlineData("x", "", CandleInterval.OneHour, AgentMode.Paper, "symbol")]
    [InlineData("x", "btc-usdt", CandleInterval.OneHour, AgentMode.Paper, "symbol")]
    [InlineData("x", "BTCUSDT", CandleInterval.OneWeek, AgentMode.Paper, "timeframe")]
    [InlineData("x", "BTCUSDT", CandleInterval.OneHour, AgentMode.Live, "exchangeKeyId")]
    public void RejectsBadSettings(string name, string symbol, CandleInterval timeframe, AgentMode mode, string field)
    {
        var errors = new List<FieldError>();

        var settings = AgentRules.NormalizeSettings(name, MarketType.Spot, symbol, timeframe, mode, null, null, null, false, Caps, errors);

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Field == field);
    }

    [Fact]
    public void ANameLongerThanTheColumnIsRejected()
    {
        var errors = new List<FieldError>();
        AgentRules.NormalizeSettings(new string('n', 101), MarketType.Spot, "BTCUSDT", CandleInterval.OneHour, AgentMode.Paper, null, null, null, false, Caps, errors);
        Assert.Equal("name", Assert.Single(errors).Field);
    }

    [Fact]
    public void TheCouncilNeedsEveryRoleOnceAnEnabledRiskManagerAndModelsForEnabledRoles()
    {
        var valid = new CouncilInput(
        [
            new CouncilRoleInput(CouncilRole.MarketAnalyst, AiProvider.Gpt, "gpt-x", true),
            new CouncilRoleInput(CouncilRole.SentimentFlow, AiProvider.Grok, "", false),
            new CouncilRoleInput(CouncilRole.CrossMarketContext, AiProvider.Gemini, "gemini-x", true),
            new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, "claude-x", true),
        ]);
        Assert.Empty(AgentRules.ValidateCouncil(valid));

        var missing = new CouncilInput(valid.Roles.Take(3).ToList());
        Assert.Equal("council", Assert.Single(AgentRules.ValidateCouncil(missing)).Field);

        var duplicate = new CouncilInput([.. valid.Roles.Take(3), valid.Roles[0]]);
        Assert.Equal("council", Assert.Single(AgentRules.ValidateCouncil(duplicate)).Field);

        var disabledRiskManager = new CouncilInput([.. valid.Roles.Take(3), new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, "claude-x", false)]);
        Assert.Equal("council.riskManager.enabled", Assert.Single(AgentRules.ValidateCouncil(disabledRiskManager)).Field);

        var noModel = new CouncilInput([.. valid.Roles.Take(3), new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, "  ", true)]);
        Assert.Equal("council.riskManager.model", Assert.Single(AgentRules.ValidateCouncil(noModel)).Field);

        var tooLong = new CouncilInput([.. valid.Roles.Take(3), new CouncilRoleInput(CouncilRole.RiskManager, AiProvider.Claude, new string('m', 201), true)]);
        Assert.Equal("council.riskManager.model", Assert.Single(AgentRules.ValidateCouncil(tooLong)).Field);
    }

    [Fact]
    public void RiskLimitsStayWithinTheCaps()
    {
        Assert.Empty(AgentRules.ValidateRiskLimits(new RiskLimitsInput(1m, 1, 3m, null), Caps));
        Assert.Empty(AgentRules.ValidateRiskLimits(new RiskLimitsInput(2m, 5, 10m, 1000m), Caps));

        Assert.Equal("riskLimits.riskPerTradePercent", Assert.Single(AgentRules.ValidateRiskLimits(new RiskLimitsInput(2.5m, 1, 3m, null), Caps)).Field);
        Assert.Equal("riskLimits.riskPerTradePercent", Assert.Single(AgentRules.ValidateRiskLimits(new RiskLimitsInput(0m, 1, 3m, null), Caps)).Field);
        Assert.Equal("riskLimits.maxOpenPositions", Assert.Single(AgentRules.ValidateRiskLimits(new RiskLimitsInput(1m, 6, 3m, null), Caps)).Field);
        Assert.Equal("riskLimits.dailyLossLimitPercent", Assert.Single(AgentRules.ValidateRiskLimits(new RiskLimitsInput(1m, 1, 11m, null), Caps)).Field);
        Assert.Equal("riskLimits.maxNotional", Assert.Single(AgentRules.ValidateRiskLimits(new RiskLimitsInput(1m, 1, 3m, 0m), Caps)).Field);
    }

    [Fact]
    public void EntryWindowsNeedTheirFieldsAndANonEmptySpan()
    {
        var now = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var errors = new List<FieldError>();

        var recurring = AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.Recurring, StartDay: DayOfWeek.Friday, StartTime: new TimeOnly(21, 0, 30, 500), EndDay: DayOfWeek.Monday, EndTime: new TimeOnly(1, 0)), now, errors);
        Assert.Empty(errors);
        Assert.NotNull(recurring);
        Assert.Equal(EntryWindowKind.Recurring, recurring.Kind);
        Assert.Equal(new TimeOnly(21, 0, 30), recurring.StartTime);
        Assert.Null(recurring.StartAt);

        var oneOff = AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.OneOff, new DateTimeOffset(2026, 1, 6, 14, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 1, 6, 16, 0, 0, TimeSpan.FromHours(2))), now, errors);
        Assert.Empty(errors);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 12, 0, 0, TimeSpan.Zero), oneOff!.StartAt);
        Assert.Equal(TimeSpan.Zero, oneOff.StartAt!.Value.Offset);
        Assert.Null(oneOff.StartDay);

        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.OneOff), now, errors));
        Assert.Equal(["entryWindow.startAt", "entryWindow.endAt"], errors.Select(e => e.Field));
        errors.Clear();
        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.OneOff, now.AddDays(2), now.AddDays(1)), now, errors));
        Assert.Equal("entryWindow.endAt", Assert.Single(errors).Field);
        errors.Clear();
        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.OneOff, now.AddDays(-2), now.AddDays(-1)), now, errors));
        Assert.Contains("already ended", Assert.Single(errors).Message);
        errors.Clear();
        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.Recurring, StartDay: DayOfWeek.Monday, EndDay: DayOfWeek.Monday), now, errors));
        Assert.Equal(["entryWindow.startTime", "entryWindow.endTime"], errors.Select(e => e.Field));
        errors.Clear();
        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput(EntryWindowKind.Recurring, StartDay: DayOfWeek.Monday, StartTime: new TimeOnly(9, 0), EndDay: DayOfWeek.Monday, EndTime: new TimeOnly(9, 0)), now, errors));
        Assert.Contains("not be empty", Assert.Single(errors).Message);
        errors.Clear();
        Assert.Null(AgentRules.NormalizeEntryWindow(new EntryWindowInput((EntryWindowKind)9), now, errors));
        Assert.Equal("entryWindow.kind", Assert.Single(errors).Field);

        Assert.True(AgentRules.SameWindow(recurring, new EntryWindow { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Friday, StartTime = new TimeOnly(21, 0, 30), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(1, 0) }));
        Assert.False(AgentRules.SameWindow(recurring, oneOff));
    }

    [Fact]
    public void AppendixBDefaultsAndRoleNames()
    {
        Assert.Equal(AiProvider.Gpt, AgentRules.DefaultProvider(CouncilRole.MarketAnalyst));
        Assert.Equal(AiProvider.Grok, AgentRules.DefaultProvider(CouncilRole.SentimentFlow));
        Assert.Equal(AiProvider.Gemini, AgentRules.DefaultProvider(CouncilRole.CrossMarketContext));
        Assert.Equal(AiProvider.Claude, AgentRules.DefaultProvider(CouncilRole.RiskManager));
        Assert.Equal("riskManager", AgentRules.CamelCase(CouncilRole.RiskManager));
        Assert.Equal("Sentiment & Flow", AgentRules.Display(CouncilRole.SentimentFlow));
    }
}
