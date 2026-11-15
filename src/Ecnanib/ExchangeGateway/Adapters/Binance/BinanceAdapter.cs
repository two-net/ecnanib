using System.Globalization;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// Binance, behind the common interface: Spot and USDⓈ-margined perpetual Futures, each on the
/// real exchange and in Binance's test environment, at the addresses of the configuration file
/// (FR-BIN-1, FR-BIN-2). It reads the clock of a market, asks about a credential, and reads what
/// an account holds. It has no operation that moves funds (FR-BIN-9). See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BinanceCall"/> makes every call, and the calls it can make are those of
/// <see cref="BinanceEndpoint"/>. What is here is what each answer says, in the words of the
/// gateway: nothing of Binance's own format leaves this class.
/// </para>
/// <para>
/// What a credential may do is asked differently in the two environments, because Binance's
/// test environment has no call that reports the permissions of a key. See
/// <see cref="CheckKeyAsync"/>.
/// </para>
/// </remarks>
internal sealed class BinanceAdapter(BinanceCall call) : IExchangeAdapter
{
    // The first and the last moment that a count of milliseconds can name as a time.
    private static readonly long EarliestTime = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long LatestTime = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public async Task<ExchangeTime> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken)
    {
        var endpoint = market switch
        {
            Market.Spot => BinanceEndpoint.SpotTime,
            Market.Futures => BinanceEndpoint.FuturesTime,
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        var answer = await call.AskAsync(endpoint, mode, ServerTime, cancellationToken);

        return new ExchangeTime(answer.Outcome, answer.Value);
    }

    /// <summary>
    /// Asks Binance about a credential. On the real exchange it is one call, to the permissions
    /// of the API key, which says everything: whether the key can withdraw, whether it is
    /// restricted to addresses, and whether it may trade on each market. The test environment has
    /// no such call, so there the account of each market is read, which says whether the
    /// credential may trade on it, and nothing about withdrawals or addresses.
    /// </summary>
    public Task<ExchangeKeyCheck> CheckKeyAsync(TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        return mode switch
        {
            TradingMode.Live => CheckLiveKeyAsync(apiKey, secretKey, cancellationToken),
            TradingMode.Demo => CheckDemoKeyAsync(apiKey, secretKey, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>
    /// Reads what the account of a credential holds on one market. On Spot it is the account,
    /// asked without the assets it holds none of. On Futures it is the wallet of the market.
    /// </summary>
    public async Task<ExchangeBalances> GetBalancesAsync(
        Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        var answer = market switch
        {
            Market.Spot => await call.AskSignedAsync(
                BinanceEndpoint.SpotAccount, mode, apiKey, secretKey, SpotBalances, cancellationToken),
            Market.Futures => await call.AskSignedAsync(
                BinanceEndpoint.FuturesBalances, mode, apiKey, secretKey, FuturesBalances, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        return new ExchangeBalances(answer.Outcome, answer.Value ?? []);
    }

    private async Task<ExchangeKeyCheck> CheckLiveKeyAsync(string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var answer = await call.AskSignedAsync(
            BinanceEndpoint.KeyPermissions, TradingMode.Live, apiKey, secretKey, Permissions, cancellationToken);

        return answer.Value ?? new ExchangeKeyCheck(VerdictOf(answer.Outcome));
    }

    private async Task<ExchangeKeyCheck> CheckDemoKeyAsync(string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var spot = await call.AskSignedAsync(
            BinanceEndpoint.SpotAccount, TradingMode.Demo, apiKey, secretKey, MayTrade, cancellationToken);
        var futures = await call.AskSignedAsync(
            BinanceEndpoint.FuturesAccountSettings, TradingMode.Demo, apiKey, secretKey, MayTrade, cancellationToken);

        var onSpot = VerdictOf(spot.Outcome);
        var onFutures = VerdictOf(futures.Outcome);

        // A market that did not answer leaves its permission unknown, so there is no verdict.
        if (onSpot == KeyVerdict.Unreachable || onFutures == KeyVerdict.Unreachable)
        {
            return ExchangeKeyCheck.Unreachable;
        }

        if (onSpot == KeyVerdict.Refused && onFutures == KeyVerdict.Refused)
        {
            return ExchangeKeyCheck.Refused;
        }

        // A market that refuses a credential which the other one takes is a market the
        // credential may not trade on.
        return new ExchangeKeyCheck(
            KeyVerdict.Accepted,
            SpotTrading: spot.Value ?? false,
            FuturesTrading: futures.Value ?? false);
    }

    /// <summary>
    /// What the end of a call says about the credential it was signed with. Only Binance's answer
    /// to a wrong credential is a refusal. Anything else that is not a success says nothing about
    /// it: none of them may turn a stored key invalid.
    /// </summary>
    private static KeyVerdict VerdictOf(ExchangeCallOutcome outcome) => outcome switch
    {
        ExchangeCallOutcome.Succeeded => KeyVerdict.Accepted,
        ExchangeCallOutcome.AuthenticationFailed => KeyVerdict.Refused,
        _ => KeyVerdict.Unreachable,
    };

    /// <summary>The time that an answer of a clock gives, or null when it gives none.</summary>
    private static DateTimeOffset? ServerTime(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object
            && answer.TryGetProperty("serverTime", out var time)
            && time.ValueKind == JsonValueKind.Number
            && time.TryGetInt64(out var milliseconds)
            && milliseconds >= EarliestTime
            && milliseconds <= LatestTime
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;

    /// <summary>What the real exchange says a key may do, or null when the answer does not say whether it can withdraw.</summary>
    private static ExchangeKeyCheck? Permissions(JsonElement answer)
    {
        // Without the word on withdrawals there is no verdict: a key is never taken for one that
        // cannot withdraw because the answer did not say (FR-KEY-4, NFR-LEG-2).
        if (Flag(answer, "enableWithdrawals") is not { } canWithdraw)
        {
            return null;
        }

        return new ExchangeKeyCheck(
            KeyVerdict.Accepted,
            SpotTrading: Flag(answer, "enableSpotAndMarginTrading") ?? false,
            FuturesTrading: Flag(answer, "enableFutures") ?? false,
            CanWithdraw: canWithdraw,
            IpRestricted: Flag(answer, "ipRestrict"));
    }

    /// <summary>
    /// Whether an account of the test environment may trade, or null when the answer is not an
    /// account. An account that does not say is one that may not.
    /// </summary>
    private static bool? MayTrade(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object ? Flag(answer, "canTrade") ?? false : null;

    /// <summary>What the account of the Spot market holds, or null when the answer is not that account.</summary>
    private static List<ExchangeBalance>? SpotBalances(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("balances", out var balances)
            ? BalancesOf(balances, total: null, "free", "locked")
            : null;

    /// <summary>What the wallet of the Futures market holds, or null when the answer is not its list.</summary>
    private static List<ExchangeBalance>? FuturesBalances(JsonElement answer) =>
        BalancesOf(answer, total: "balance", "availableBalance", locked: null);

    /// <summary>
    /// The balances of a list, each asset once, without the assets that the account holds none
    /// of. It is null when a row of the list does not read: a part of a list would make the rest
    /// look empty.
    /// </summary>
    /// <param name="rows">The list, with an asset in each row.</param>
    /// <param name="total">The name of the whole amount of a row, or null where it is what is available and what is locked together.</param>
    /// <param name="available">The name of the amount that a new order can use.</param>
    /// <param name="locked">The name of the amount that open orders hold, where the whole amount is not given.</param>
    private static List<ExchangeBalance>? BalancesOf(JsonElement rows, string? total, string available, string? locked)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var balances = new List<ExchangeBalance>();
        var assets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("asset", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } asset
                || !assets.Add(asset)
                || Amount(row, available) is not { } free)
            {
                return null;
            }

            var whole = total is not null ? Amount(row, total) : free + Amount(row, locked!);

            if (whole is not { } all)
            {
                return null;
            }

            // An asset that is not among them is one the account holds none of.
            if (all != 0 || free != 0)
            {
                balances.Add(new ExchangeBalance(asset, all, free));
            }
        }

        return balances;
    }

    /// <summary>An amount of a row, which Binance writes as text so that no digit is lost, or null when the row has none.</summary>
    private static decimal? Amount(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                value.GetString(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
                ? amount
                : null;

    /// <summary>A yes or no of an answer, or null when the answer does not have it.</summary>
    private static bool? Flag(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
