using System.Text;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Exports;

/// <summary>
/// The two files that a user takes their trades and their decisions out of the service as
/// (FR-RPT-4): what columns each has, and what a row of each says. See "The files" in
/// docs/export.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row says its mode,</b> as the word <c>Demo</c> or <c>Live</c>, and a file adds
/// nothing up: what was done in Demo is labeled as Demo, and is never combined with what was
/// done in Live (FR-BIN-11).
/// </para>
/// <para>
/// <b>No column is of a secret,</b> and none could be: what is read for a file is what the
/// screen of an agent shows (FR-KEY-8).
/// </para>
/// </remarks>
public static class ExportFiles
{
    /// <summary>What a file says it is: comma-separated values, in UTF-8.</summary>
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>What the two downloads are, above their links.</summary>
    public const string Explained =
        "The trades and the decisions of your agents are downloads of their own, as CSV files that a spreadsheet opens. " +
        "Each holds every agent of yours, and each row says whether it is of Demo or of Live.";

    /// <summary>The words of the link to the file of the trades.</summary>
    public const string TradesLink = "Download your trades (CSV)";

    /// <summary>The words of the link to the file of the decisions.</summary>
    public const string DecisionsLink = "Download your decisions (CSV)";

    // With the mark that says so at its start: a spreadsheet reads a name in any script right.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    // The roles in their order, each with the start of the names of its columns. They stand
    // before the columns, which are made from them: what is written first is made first.
    private static IReadOnlyList<(Role Role, string Column)> Roles { get; } =
    [
        (Role.MarketAnalyst, "market_analyst"),
        (Role.SentimentAnalyst, "sentiment_analyst"),
        (Role.Strategist, "strategist"),
        (Role.RiskManager, "risk_manager"),
    ];

    /// <summary>The columns of the file of the trades, in their order.</summary>
    public static IReadOnlyList<string> TradeColumns { get; } =
    [
        "position_id", "agent_id", "agent", "mode", "market", "symbol", "timeframe", "side", "quantity", "entry_price",
        "exit_price", "stop_loss", "take_profit", "opened_at", "closed_at", "exit_reason", "asset", "net_result", "fees",
        "funding", "result_in_r", "held_seconds", "estimate", "entry_cycle_id",
        .. Roles.Select(role => $"{role.Column}_model"),
    ];

    /// <summary>The columns of the file of the decisions, in their order.</summary>
    public static IReadOnlyList<string> DecisionColumns { get; } =
    [
        "cycle_id", "agent_id", "agent", "mode", "kind", "market", "symbol", "timeframe", "candle_closed_at", "started_at",
        "ended_at", "outcome", "no_entry_reason",
        .. Roles.SelectMany(role => new[] { $"{role.Column}_model", $"{role.Column}_call", $"{role.Column}_answer" }),
        "risk_guard_verdicts", "risk_guard_failed_rules", "risk_guard_quantity", "risk_guard_leverage", "orders",
    ];

    /// <summary>The row of a trade: a closed position, with its result once that is recorded.</summary>
    public static IReadOnlyList<string> Cells(ExportedTrade exported)
    {
        ArgumentNullException.ThrowIfNull(exported);

        var trade = exported.Trade;
        var result = trade.Result;

        return
        [
            CsvText.Id(trade.PositionId),
            CsvText.Id(exported.AgentId),
            CsvText.Text(exported.AgentName),
            CsvText.Name<TradingMode>(trade.Mode),
            CsvText.Name<Market>(trade.Market),
            CsvText.Text(trade.Symbol),
            exported.Timeframe.Code(),
            CsvText.Name<PositionSide>(trade.Side),
            CsvText.Number(trade.Quantity),
            CsvText.Number(trade.EntryPrice),
            CsvText.Number(trade.ExitPrice),
            CsvText.Number(exported.StopLoss),
            CsvText.Number(exported.TakeProfit),
            CsvText.Time(trade.OpenedAt),
            CsvText.Time(trade.ClosedAt),
            CsvText.Name(trade.ExitReason),
            CsvText.Text(trade.Asset),

            // A result is recorded some time after the close: until then the cells are empty.
            CsvText.Number(result?.NetResult),
            CsvText.Number(result?.Fees),
            CsvText.Number(result?.Funding),
            CsvText.Number(result?.ResultInR),
            CsvText.Number(result is null ? null : (long)result.Held.TotalSeconds),
            CsvText.Whether(trade.Estimate),
            CsvText.Id(exported.EntryCycleId),
            .. Roles.Select(role => exported.Team
                .Where(held => held.Role == role.Role)
                .Select(held => CsvText.Text(Model(held.ProviderName, held.ModelIdentifier)))
                .FirstOrDefault(string.Empty)),
        ];
    }

    /// <summary>The row of a decision: a recorded cycle, with what each role answered and what the Risk Guard made of the plan.</summary>
    public static IReadOnlyList<string> Cells(ExportedDecision exported)
    {
        ArgumentNullException.ThrowIfNull(exported);

        var cycle = exported.Record;

        // What the cycle was for is what its data package says, which a cycle that ended early kept none of.
        var package = cycle.DataPackage;

        // The check that computed the size of an order: the one of a plan that opens a position.
        var sized = cycle.Checks.FirstOrDefault(check => check.Quantity is not null);

        // The rules that each check found in the way of the plan: one that does not hold, and one that is not known.
        var inTheWay = cycle.Checks
            .Select(check => string.Join(
                ' ',
                check.Rules.Where(rule => rule.Outcome is RuleOutcome.Failed or RuleOutcome.Unknown).Select(rule => $"{rule.Rule}:{rule.Outcome}")))
            .ToList();

        return
        [
            CsvText.Id(cycle.Id),
            CsvText.Id(exported.AgentId),
            CsvText.Text(exported.AgentName),
            CsvText.Name<TradingMode>(cycle.Mode),
            CsvText.Name<CycleKind>(cycle.Kind),
            CsvText.Name(package?.Market),
            CsvText.Text(package?.Symbol),
            package?.Timeframe.Code() ?? string.Empty,
            CsvText.Time(cycle.ClosedAt),
            CsvText.Time(cycle.StartedAt),
            CsvText.Time(cycle.EndedAt),
            CsvText.Name<CycleOutcome>(cycle.Outcome),
            CsvText.Name(cycle.NoEntry),
            .. Roles.SelectMany(role =>
            {
                // The calls are in the order they were made: the last of a role is the one that counted.
                var call = cycle.Calls.LastOrDefault(made => made.Role == role.Role);

                return new[]
                {
                    call is null ? string.Empty : CsvText.Text(Model(call.ProviderName, call.ModelIdentifier)),
                    CsvText.Name(call?.Outcome),
                    CsvText.Text(call?.Result),
                };
            }),

            // One for each check, in the order of the checks: the second is of the close of a
            // reverse that was rejected. Where nothing was in the way of any, the cell is empty.
            string.Join(';', cycle.Checks.Select(check => check.Verdict)),
            inTheWay.All(string.IsNullOrEmpty) ? string.Empty : string.Join(';', inTheWay),
            CsvText.Number(sized?.Quantity),
            CsvText.Number((long?)sized?.Leverage),
            string.Join(';', cycle.Orders.Select(order => $"{order.Purpose}:{order.Status}")),
        ];
    }

    /// <summary>Writes a file: the mark of UTF-8, the line of the columns, and a line for each row as it is read.</summary>
    /// <param name="body">What the file is written to.</param>
    /// <param name="columns">The columns of the file.</param>
    /// <param name="rows">The rows, which are read a part at a time.</param>
    /// <param name="cells">What a row says, cell by cell, in the order of the columns.</param>
    /// <param name="cancellationToken">Cancels the reads and the writing.</param>
    public static async Task WriteAsync<TRow>(
        Stream body,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<TRow> rows,
        Func<TRow, IReadOnlyList<string>> cells,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(cells);

        // It is not held as a whole: what is written goes on as it fills, and the rest is read after.
        await using var file = new StreamWriter(body, Utf8, bufferSize: 64 * 1024, leaveOpen: true);

        await file.WriteAsync(CsvText.Line(columns).AsMemory(), cancellationToken);

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            await file.WriteAsync(CsvText.Line(cells(row)).AsMemory(), cancellationToken);
        }

        await file.FlushAsync(cancellationToken);
    }

    // A model as one cell that tells it from every other: the name of its provider, and its identifier there.
    private static string Model(string providerName, string identifier) => $"{providerName} {identifier}";
}
