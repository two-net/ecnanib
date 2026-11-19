using System.Globalization;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// Binance, behind the common interface: Spot and USDⓈ-margined perpetual Futures, each on the
/// real exchange and in Binance's test environment, at the addresses of the configuration file
/// (FR-BIN-1, FR-BIN-2). It reads the clock of a market, asks about a credential, reads what an
/// account holds, reads how an account stands on the Futures market (FR-BIN-F2), and reads the
/// position of an account there with its margin, and the funding that the account received and
/// paid (FR-BIN-F4, FR-BIN-F5). It has no operation that moves funds (FR-BIN-9), and none that
/// adds margin to a position. See docs/exchange.md.
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
        var answer = await call.AskAsync(BinanceEndpoint.TimeOf(market), mode, ServerTime, cancellationToken);

        return new ExchangeTime(answer.Outcome, answer.Value);
    }

    /// <summary>
    /// Asks Binance about a credential. On the real exchange it is one call, to the permissions
    /// of the API key, which says everything: whether the key can withdraw, whether it is
    /// restricted to addresses, and whether it may trade on each market. The test environment has
    /// no such call, so there the account of each market is read, which says whether the
    /// credential may trade on it, and nothing about withdrawals or addresses.
    /// </summary>
    public Task<ExchangeKeyCheck> CheckKeyAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        return mode switch
        {
            TradingMode.Live => CheckLiveKeyAsync(userId, apiKey, secretKey, cancellationToken),
            TradingMode.Demo => CheckDemoKeyAsync(userId, apiKey, secretKey, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>
    /// Reads what the account of a credential holds on one market. On Spot it is the account,
    /// asked without the assets it holds none of. On Futures it is the wallet of the market.
    /// </summary>
    public async Task<ExchangeBalances> GetBalancesAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        var answer = market switch
        {
            Market.Spot => await call.AskSignedAsync(
                BinanceEndpoint.SpotAccount, mode, userId, apiKey, secretKey, SpotBalances, cancellationToken),
            Market.Futures => await call.AskSignedAsync(
                BinanceEndpoint.FuturesBalances, mode, userId, apiKey, secretKey, FuturesBalances, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        return new ExchangeBalances(answer.Outcome, answer.Value ?? []);
    }

    /// <summary>
    /// Reads how the account of a credential stands on the Futures market for one symbol: its
    /// settings, which say the two modes that hold for the whole account, and then its positions
    /// in the symbol. The first that does not answer is how the read ended.
    /// </summary>
    public async Task<ExchangeFuturesAccount> GetFuturesAccountAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        var settings = await call.AskSignedAsync(
            BinanceEndpoint.FuturesAccountSettings, mode, userId, apiKey, secretKey, Modes, cancellationToken);

        if (settings.Value is not { } modes)
        {
            return ExchangeFuturesAccount.Without(settings.Outcome);
        }

        var positions = await call.AskSignedAsync(
            BinanceEndpoint.PositionsOf(symbol), mode, userId, apiKey, secretKey, answer => HoldsPosition(answer, symbol), cancellationToken);

        return positions.Value is { } holds
            ? new ExchangeFuturesAccount(positions.Outcome, modes.Hedge, modes.MultiAssets, holds)
            : ExchangeFuturesAccount.Without(positions.Outcome);
    }

    /// <summary>
    /// Reads the position that the account of a credential holds in one symbol of the Futures
    /// market, with its mark price and the price that Binance liquidates it at: the positions of
    /// the account in the symbol, which is one call.
    /// </summary>
    public async Task<ExchangeFuturesPosition> GetFuturesPositionAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        var answer = await call.AskSignedAsync(
            BinanceEndpoint.PositionsOf(symbol), mode, userId, apiKey, secretKey, rows => PositionIn(rows, symbol), cancellationToken);

        return answer.Value ?? ExchangeFuturesPosition.Without(answer.Outcome);
    }

    /// <summary>
    /// Reads the funding that the account of a credential received and paid in one symbol of
    /// the Futures market between two moments: the rows of its income that are funding, which is
    /// one call. Binance gives a thousand rows at most, so an answer of a thousand may not be
    /// all of it.
    /// </summary>
    public async Task<ExchangeFundings> GetFundingAsync(
        Guid userId,
        TradingMode mode,
        string apiKey,
        string secretKey,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        if (to < from)
        {
            throw new ArgumentException("The last moment that is asked for is before the first.", nameof(to));
        }

        var answer = await call.AskSignedAsync(
            BinanceEndpoint.FundingOf(symbol, from, to), mode, userId, apiKey, secretKey, rows => FundingIn(rows, symbol), cancellationToken);

        return answer.Value is { } fundings
            ? new ExchangeFundings(answer.Outcome, fundings, More: fundings.Count >= BinanceEndpoint.FundingRows)
            : ExchangeFundings.Without(answer.Outcome);
    }

    private async Task<ExchangeKeyCheck> CheckLiveKeyAsync(
        Guid userId, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var answer = await call.AskSignedAsync(
            BinanceEndpoint.KeyPermissions, TradingMode.Live, userId, apiKey, secretKey, Permissions, cancellationToken);

        return answer.Value ?? new ExchangeKeyCheck(VerdictOf(answer.Outcome));
    }

    private async Task<ExchangeKeyCheck> CheckDemoKeyAsync(
        Guid userId, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var spot = await call.AskSignedAsync(
            BinanceEndpoint.SpotAccount, TradingMode.Demo, userId, apiKey, secretKey, MayTrade, cancellationToken);
        var futures = await call.AskSignedAsync(
            BinanceEndpoint.FuturesAccountSettings, TradingMode.Demo, userId, apiKey, secretKey, MayTrade, cancellationToken);

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
    /// it, a call that the limit on requests kept from Binance included: none of them may turn a
    /// stored key invalid.
    /// </summary>
    private static KeyVerdict VerdictOf(ExchangeCallOutcome outcome) => outcome switch
    {
        ExchangeCallOutcome.Succeeded => KeyVerdict.Accepted,
        ExchangeCallOutcome.AuthenticationFailed => KeyVerdict.Refused,
        _ => KeyVerdict.Unreachable,
    };

    /// <summary>
    /// The time that an answer of a clock gives, or null when it gives none. The watch on the
    /// clock reads the same answer: see <see cref="BinanceClockJob"/>.
    /// </summary>
    internal static DateTimeOffset? ServerTime(JsonElement answer) =>
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

    /// <summary>
    /// The two modes of the account of the Futures market that hold for all of its symbols, or
    /// null when the answer does not say both: an account that does not say its mode is not
    /// taken for one in one-way mode with one asset.
    /// </summary>
    private static (bool Hedge, bool MultiAssets)? Modes(JsonElement answer) =>
        Flag(answer, "dualSidePosition") is { } hedge && Flag(answer, "multiAssetsMargin") is { } multiAssets
            ? (hedge, multiAssets)
            : null;

    /// <summary>
    /// Whether the positions of an account say one in a symbol, or null when the answer is not
    /// their list or a row of it does not read. Binance lists a symbol that has a position or
    /// orders, with an amount of nothing where it has orders only, so a list without the symbol
    /// says that the account holds nothing in it. In hedge mode a symbol has a row for each side.
    /// </summary>
    private static bool? HoldsPosition(JsonElement answer, string symbol)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var holds = false;

        foreach (var row in answer.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("symbol", out var name)
                || name.ValueKind != JsonValueKind.String
                || Amount(row, "positionAmt") is not { } amount)
            {
                return null;
            }

            holds |= name.ValueEquals(symbol) && amount != 0m;
        }

        return holds;
    }

    /// <summary>
    /// The position that the positions of an account say in a symbol, with how its margin
    /// stands, or null when the answer is not their list, a row of it does not read, or two
    /// rows of the symbol hold something: in hedge mode a symbol has a row for each side, and
    /// two that hold are no one position. A list in which no row of the symbol holds anything
    /// says that the account holds no position in it.
    /// </summary>
    private static ExchangeFuturesPosition? PositionIn(JsonElement answer, string symbol)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ExchangeFuturesPosition? position = null;

        foreach (var row in answer.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("symbol", out var name)
                || name.ValueKind != JsonValueKind.String
                || Amount(row, "positionAmt") is not { } amount)
            {
                return null;
            }

            if (!name.ValueEquals(symbol) || amount == 0m)
            {
                continue;
            }

            // The prices of a position are above nothing. Binance writes a liquidation price of
            // nothing where a position has none.
            if (position is not null
                || Amount(row, "entryPrice") is not { } entryPrice
                || entryPrice <= 0m
                || Amount(row, "markPrice") is not { } markPrice
                || markPrice <= 0m
                || Amount(row, "liquidationPrice") is not { } liquidationPrice
                || liquidationPrice < 0m)
            {
                return null;
            }

            position = new ExchangeFuturesPosition(
                ExchangeCallOutcome.Succeeded, amount, entryPrice, markPrice, liquidationPrice == 0m ? null : liquidationPrice);
        }

        return position ?? ExchangeFuturesPosition.Without(ExchangeCallOutcome.Succeeded);
    }

    /// <summary>
    /// The funding of a list of income, oldest first, or null when the answer is not that list,
    /// or a row of it does not read or is not funding in the symbol that was asked for: a part
    /// of a list would make the rest look as if it had not been paid.
    /// </summary>
    private static List<ExchangeFunding>? FundingIn(JsonElement answer, string symbol)
    {
        if (answer.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fundings = new List<ExchangeFunding>();

        foreach (var row in answer.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("symbol", out var name)
                || name.ValueKind != JsonValueKind.String
                || !name.ValueEquals(symbol)
                || !row.TryGetProperty("incomeType", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.ValueEquals("FUNDING_FEE")
                || Amount(row, "income") is not { } amount
                || !row.TryGetProperty("asset", out var assetName)
                || assetName.ValueKind != JsonValueKind.String
                || assetName.GetString() is not { Length: > 0 } asset
                || !row.TryGetProperty("time", out var time)
                || Moment(time) is not { } at
                || !row.TryGetProperty("tranId", out var number)
                || TransactionId(number) is not { } transactionId)
            {
                return null;
            }

            fundings.Add(new ExchangeFunding(transactionId, amount, asset, at));
        }

        // Oldest first, whatever order Binance gave them in, and those of one moment by their number.
        return [.. fundings.OrderBy(funding => funding.At).ThenBy(funding => funding.TransactionId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The number that Binance gave a row of income, as text, or null when the row has none.
    /// Binance writes it as a number, which is kept digit for digit.
    /// </summary>
    private static string? TransactionId(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out _) => value.GetRawText(),
        JsonValueKind.String when value.GetString() is { Length: > 0 } text => text,
        _ => null,
    };

    /// <summary>The time that a count of milliseconds names, or null when it is none.</summary>
    private static DateTimeOffset? Moment(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var milliseconds)
            && milliseconds >= EarliestTime
            && milliseconds <= LatestTime
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;

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
