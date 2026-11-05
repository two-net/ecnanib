using System.Globalization;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// The RiskCaps section (SRS 7.3, FR-RISK-11). Read() is the only way in. A missing value takes SRS 7.3's, and an
/// invalid one stops startup with a message that names it (CFG-03).
/// </summary>
internal static class RiskCapsSettings
{
    public const string Section = "RiskCaps";

    public const string MaxLeverageKey = Section + ":MaxLeverage";

    public const string MaxRiskPerTradePercentKey = Section + ":MaxRiskPerTradePercent";

    public const string MinLiquidationBufferMultipleKey = Section + ":MinLiquidationBufferMultiple";

    public const string MinRewardToRiskKey = Section + ":MinRewardToRisk";

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static RiskCaps Read(IConfiguration configuration)
    {
        var defaults = RiskCaps.Default;

        return new RiskCaps
        {
            MaxLeverage = Whole(configuration, MaxLeverageKey, defaults.MaxLeverage, 1, RiskLimits.MaxLeverage),
            MaxRiskPerTradePercent = Number(configuration, MaxRiskPerTradePercentKey, defaults.MaxRiskPerTradePercent, 0m, inclusiveMinimum: false, RiskLimits.MaxPercent),
            MinLiquidationBufferMultiple = Number(configuration, MinLiquidationBufferMultipleKey, defaults.MinLiquidationBufferMultiple, 1m, inclusiveMinimum: true, RiskLimits.MaxMultiple),
            MinRewardToRisk = Number(configuration, MinRewardToRiskKey, defaults.MinRewardToRisk, 0m, inclusiveMinimum: false, RiskLimits.MaxMultiple),
        };
    }

    private static int Whole(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{key} must be a whole number.");
        }

        if (number < minimum || number > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }

        return number;
    }

    private static decimal Number(IConfiguration configuration, string key, decimal fallback, decimal minimum, bool inclusiveMinimum, decimal maximum)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{key} must be a number.");
        }

        if ((inclusiveMinimum ? number < minimum : number <= minimum) || number > maximum)
        {
            var lower = inclusiveMinimum ? $"at least {minimum.ToString(CultureInfo.InvariantCulture)}" : $"above {minimum.ToString(CultureInfo.InvariantCulture)}";

            throw new InvalidOperationException($"{key} must be {lower} and at most {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return number;
    }
}

/// <summary>
/// Says at startup which caps apply. It takes <see cref="RiskCaps"/>, and the host builds every hosted service
/// before it starts any, so an invalid cap stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class RiskEngineStartup(RiskCaps caps, ILogger<RiskEngineStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Risk caps: leverage up to {MaxLeverage}x, risk per trade up to {MaxRiskPerTradePercent}%, liquidation buffer at least {MinLiquidationBufferMultiple}x, reward-to-risk at least {MinRewardToRisk}",
            caps.MaxLeverage,
            caps.MaxRiskPerTradePercent,
            caps.MinLiquidationBufferMultiple,
            caps.MinRewardToRisk);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
