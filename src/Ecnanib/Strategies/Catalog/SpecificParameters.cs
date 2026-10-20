namespace Ecnanib.Strategies.Catalog;

/// <summary>Names of the strategy-specific parameters (Appendix A), in the camelCase shape of <c>agents.parameters</c>.</summary>
internal static class SpecificParameters
{
    public const string SwingLookback = "swingLookback";
    public const string BiasEmaPeriod = "biasEmaPeriod";
    public const string PullbackToleranceAtr = "pullbackToleranceAtr";
    public const string PullbackLookback = "pullbackLookback";
    public const string VolumeAveragePeriod = "volumeAveragePeriod";
    public const string VolumeMultiple = "volumeMultiple";
    public const string SessionStartHour = "sessionStartHour";
    public const string SessionEndHour = "sessionEndHour";
    public const string FundingRateExtreme = "fundingRateExtreme";
    public const string PartialTakeProfitR = "partialTakeProfitR";
    public const string MinRewardR = "minRewardR";
    public const string TypicalRewardR = "typicalRewardR";
    public const string MaxStopDistanceAtr = "maxStopDistanceAtr";
    public const string WeekendSizeMultiplier = "weekendSizeMultiplier";
    public const string RangeMinCandles = "rangeMinCandles";
    public const string RangeMaxHeightAtr = "rangeMaxHeightAtr";
    public const string RetestMaxCandles = "retestMaxCandles";
    public const string RequireRetest = "requireRetest";
    public const string ExpectedHoldCandles = "expectedHoldCandles";
    public const string BtcRegimeEmaPeriod = "btcRegimeEmaPeriod";
    public const string RegimeEmaPeriod = "regimeEmaPeriod";
    public const string RegimeRisingLookback = "regimeRisingLookback";
    public const string ConsolidationMinDays = "consolidationMinDays";
    public const string WeeklyCloseConfirmation = "weeklyCloseConfirmation";
    public const string MaxExtensionPercent = "maxExtensionPercent";
    public const string MinDailyQuoteVolume = "minDailyQuoteVolume";
}
