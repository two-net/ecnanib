# Export of trades and decisions

A user can export their trades and decisions as CSV (FR-RPT-4). An export contains only the data of the user who is signed in (NFR-SEC-3) and no secrets (FR-KEY-8), and Demo results are labeled as Demo and never combined with Live results (FR-BIN-11): every row says its mode, and a file adds nothing up.

There are two downloads, each of every agent of the user: the trades at `/export/trades`, and the decisions at `/export/decisions`. Nothing of them is stored: each file is written from the rows of that user when it is asked for.

What reads the rows is `IExportedRecords` in `src/Ecnanib/DecisionEngine`, with `ListedTrades` and `CycleRecordViews` beside it. What makes the files is `ExportFiles` and `CsvText` in `src/Ecnanib/WebUi/Exports`, and the two endpoints are in `WebUiModule.cs`. The rules below are enforced by `CsvExportTests` and `CsvTextTests` in `tests/Ecnanib.Tests/Export`.

What a trade is, and what its result is made of, is in "The results of a position" in [orders.md](orders.md). What a cycle is and how it ends is in [team.md](team.md), and what the Risk Guard checks in [risk-guard.md](risk-guard.md). The screen that shows the same trades and decisions is in [agent-screen.md](agent-screen.md), what else a user can download in "Exporting your data" in [accounts.md](accounts.md), and whose rows a request reaches in [ownership.md](ownership.md).

## The two downloads

| Address | File | A row is |
|---|---|---|
| `/export/trades` | `trades-<date>.csv` | A trade: a position that is closed, with its result once that is recorded. An open position is no trade yet. |
| `/export/decisions` | `decisions-<date>.csv` | A decision: a cycle that was recorded, with what each role answered and what the Risk Guard made of the plan. |

| Rule | Detail |
|---|---|
| **A file is of every agent of the user.** | Each row names its agent, by its id and by the name that it has now. A file has no choice of an agent, a mode, or a time. |
| **The latest is first.** | The trades by when they were closed, and the decisions by the close of their candle, as the screen of an agent lists them. |
| **A row says what the screen of its agent says.** | One rule makes a trade for the trade list and for the file, `ListedTrades`, and one rule makes a cycle for its timeline and for the file, `CycleRecordViews`. A file says nothing else of a trade or of a cycle than a page does (FR-RPT-2). |
| **A trade names the decision that opened it.** | `entry_cycle_id` of a trade is `cycle_id` of a decision, so the two files can be read together. A trade is kept indefinitely and a decision for a time, so a trade of long ago names a decision that the other file no longer has. |
| **A file has what is stored.** | The decisions are those of the days of `Retention:DecisionDays`, 365 in the tracked file, and a decision of longer ago than those of `Retention:TranscriptDays`, 90, has the cells of its roles empty: what the roles answered is kept for that long (NFR-DAT-5). The trades are every trade. See [retention.md](retention.md). |
| **An agent that is deleted takes its rows along.** | What hangs on an agent is deleted with it, so neither file has a row of it: see [ownership.md](ownership.md). |

## Per mode

| Rule | Detail |
|---|---|
| **Every row says its mode,** as the word `Demo` or `Live`. | The column `mode` of both files. No row is without it. |
| **It is the mode that the agent was in then.** | A position and a cycle keep saying the mode that their agent had (FR-BIN-11). A row of an agent that was switched to Live since is labeled as Demo all the same. |
| **Nothing is added up.** | A file has a row for each trade and for each decision, and no row of a total, so nothing of Demo is combined with anything of Live. Whoever adds a column up filters by `mode` first. |
| **A test cycle is among the decisions,** and says that it is one. | The column `kind` says `Scheduled` or `Test`. A test cycle placed no orders, whatever its team decided (FR-CYC-12): see "A test cycle" in [cycles.md](cycles.md). |

## The trades

One row for each closed position. The amounts are in the asset of the column `asset`.

| Column | What it says |
|---|---|
| `position_id` | The id of the position. |
| `agent_id`, `agent` | The agent whose position it was: its id, and the name that it has now. |
| `mode` | `Demo` or `Live`: the mode that the agent was in when the position was opened. |
| `market`, `symbol`, `timeframe` | `Spot` or `Futures`, the symbol, and `15m`, `1H`, `4H`, or `1D`, as the agent had them then. |
| `side` | `Long` or `Short`. |
| `quantity` | How much it held when it was opened: what was executed of its entry. Empty where no entry of the agent is stored for it. |
| `entry_price`, `exit_price` | The average price that it was entered at, and that the orders of the agent closed it at. The exit price is empty where no order of the agent closed it, and where one that did says no price. |
| `stop_loss`, `take_profit` | The prices of its protection, as they last were. The take-profit is empty when its plan named none. |
| `opened_at`, `closed_at` | When it was opened and closed. |
| `exit_reason` | How it was closed: `StopLoss`, `TakeProfit`, `TeamClose`, `UserClose`, `LossLimit`, `ProtectionFailed`, or `ClosedAtExchange`. |
| `asset` | The asset that the symbol is priced in. |
| `net_result`, `fees`, `funding` | Its net profit or loss, the fees of the executions of its orders as one sum, and what it received as funding less what it paid. |
| `result_in_r` | The net result as a multiple of R. Empty where R is not known. |
| `held_seconds` | How long it was held. |
| `estimate` | `true` where a part of it left the agent by no order of the agent, or not all of its result is what the exchange said. See "What is corrected" in [orders.md](orders.md). |
| `entry_cycle_id` | The cycle whose order opened it. Empty where no entry of the agent is stored for it. |
| `market_analyst_model`, `sentiment_analyst_model`, `strategist_model`, `risk_manager_model` | The model that held each role when the position was opened: the name of its provider, and its identifier there. Empty for a role that had none. |

A result is recorded some time after the close. Until then the five cells of the result are empty, and the position is a row all the same.

## The decisions

One row for each recorded cycle.

| Column | What it says |
|---|---|
| `cycle_id` | The id of the cycle. |
| `agent_id`, `agent` | The agent whose cycle it was: its id, and the name that it has now. |
| `mode` | `Demo` or `Live`: the mode that the agent was in then. |
| `kind` | `Scheduled` for a cycle that a candle close started, and `Test` for one that its user ran. |
| `market`, `symbol`, `timeframe` | What the cycle was for, as its data package says. Empty for a cycle that kept none, because it ended before its package was built. |
| `candle_closed_at`, `started_at`, `ended_at` | The close of the candle that the cycle was for, and when the cycle started and ended. |
| `outcome` | How it ended: `Hold`, `Unreviewed`, `Approved`, `Reduced`, `Vetoed`, `Stale`, `NoDecision`, `Failed`, `Stopped`, or `Halted`. See "The outcome of a cycle" in [team.md](team.md). |
| `no_entry_reason` | Why no entry was placed for a plan that the Risk Guard accepted. Empty otherwise. |
| `<role>_model` | For each of `market_analyst`, `sentiment_analyst`, `strategist`, and `risk_manager`: the model that was called, as the name of its provider and its identifier there. |
| `<role>_call` | How the call ended: `Succeeded`, `InvalidResult`, `TimedOut`, `AuthenticationFailed`, `ModelNotFound`, `Failed`, `NoKey`, `Canceled`, `ProviderPaused`, or `KeyPaused`. The last two are of a call that was not made while the calls were paused: see "The breakers of the AI providers" in [reliability.md](reliability.md). |
| `<role>_answer` | What the model answered, as the one line of JSON that the record keeps. Empty for a call whose answer is not kept. |
| `risk_guard_verdicts` | `Accepted` or `Rejected`, for each check of the cycle in their order, with `;` between two. Empty when nothing went on to the Risk Guard. |
| `risk_guard_failed_rules` | The rules that the plan did not pass, for each check in the same order: each as `Rule:Failed` or `Rule:Unknown`, with a space between two rules and `;` between two checks. Empty where every check passed. |
| `risk_guard_quantity`, `risk_guard_leverage` | The size and the leverage of the order that the Risk Guard computed for a plan that opens a position (FR-RSK-5). Empty for a plan that opens nothing. |
| `orders` | The orders that the cycle placed, each as `Purpose:Status` as it is now, with `;` between two: `Entry:Filled;StopLoss:Open`. Empty when it placed none. |

A role that was called twice in a cycle is in the row with its last call, which is the one that counted. A cycle has two checks only when it closed a position after a reverse was rejected: see "The record of a check" in [risk-guard.md](risk-guard.md).

## The files

A file is for a program to read, and for a person in a spreadsheet. `CsvText` is the one rule of a cell and of a line.

| Rule | Detail |
|---|---|
| **It is CSV as programs read it.** | A first line with the names of the columns, a comma between two cells, and a carriage return and a line feed at the end of each line. A cell that holds a comma, a quote, or the end of a line stands in quotes, and a quote in it is written twice. |
| **It is UTF-8, and says so at its start.** | With the byte-order mark, so that a spreadsheet reads a name in any script right. The content type is `text/csv; charset=utf-8`. |
| **A number has every digit that it has.** | With a point, no sign for thousands, no zeros after the last digit, and no word beside it: the asset is a column of its own. It is the same wherever the file is written and opened. |
| **A time is in UTC, to the second, and says so:** `2026-11-24T09:00:00Z`. | The time zone of the user is not used (NFR-USA-3): a file is the same wherever it is opened, and a program reads it. See "What stays UTC" in [times.md](times.md). |
| **A member of a fixed set is its name.** | `Demo`, `StopLoss`, `Approved`: the names that the database stores, and not the words of a page. A timeframe is its code. |
| **What is not known is an empty cell,** and never a nought. | A result that is not recorded yet, a price that no order says, a role that was not called. |
| **No cell is a formula.** | A spreadsheet works out a cell that starts with `=`, `+`, `-`, or `@`, and skips a tab or the end of a line before one. A name that a user typed, or an answer that a model wrote, may start so. Such a cell is given an apostrophe before it, which a spreadsheet shows as text. A number is written by the service and is given none, so a loss keeps its sign. |

## Whose rows, and no secret

| Rule | Detail |
|---|---|
| **It is for a signed-in user.** | A visitor is sent to sign in. It asks for no fresh proof of who the user is, as the download of the data of an account asks for none: see "Exporting your data" in [accounts.md](accounts.md). |
| **It is of the user of the session** (NFR-SEC-3). | The endpoint hands over the user who is signed in, and never one that the address names: an address has nothing that chooses whose rows are read. Every row is asked for with `OwnedBy`, the names of the agents as well. See [ownership.md](ownership.md). |
| **It holds no secret** (FR-KEY-8). | No table that is read for a file holds one, and none is read. No column is of a key, a password, a token, or the email address of the user. What a model answered is in a file as it was stored, and no prompt holds a secret or personal data (FR-PRM-9). |
| **Nothing on the way keeps it.** | A file is sent as an attachment with `Cache-Control: no-store`, and its link is marked `download`, so the browser asks for the file once. |
| **It only reads.** | Asking for a file changes no row. |
| **The log says who exported, and nothing of what.** | `User {UserId} exported their trades`, and `... their decisions`. It is no event of the audit trail, as the download of the data of an account is none. |

## How it is read

```csharp
public interface IExportedRecords
{
    const int ChunkSize = 500;

    IAsyncEnumerable<ExportedTrade> TradesOfUserAsync(Guid userId, CancellationToken cancellationToken);

    IAsyncEnumerable<ExportedDecision> DecisionsOfUserAsync(Guid userId, CancellationToken cancellationToken);
}
```

- **The Decision Engine reads, and the Web UI writes.** The Decision Engine keeps every table that a file is made from, and may not use Accounts, so it is handed the user. The Web UI says what the columns are. `ComponentRules.cs` is unchanged.
- **A file is never held as a whole.** An agent on 15m records 96 cycles a day, so a user may have more decisions than fit in memory at once. The rows are read 500 at a time and written as they are read.
- **Each part is read at one moment,** in a snapshot of its own, so a row and what hangs on it agree: see "Reads that agree" in [persistence.md](persistence.md). The snapshot is let go of before any of the part is handed over, so nothing holds the database while a slow download is written.
- **A part begins after the last row of the part before,** by the close, then the start of a cycle, then the id: no row is in a file twice, and none is left out for having the same moment as another. What is recorded while an export runs may not be in it.
- **The models are named once.** A model of the catalog is never deleted, so each is looked up the first time that a row names it, and kept for the rest of the file.

## The page

Both downloads have a link on `/account`, beside the download of the data of the account, and at the foot of the dashboard. The texts of the links are in `ExportFiles` (NFR-USA-4). An endpoint is no page: it answers with the file.

## Testing

| Test class | What it holds |
|---|---|
| `CsvExportTests` | Both files over HTTP, against the real database. The columns of each, and a row of each cell by cell: a trade as the trade list of its agent says it, and a cycle with every role's answer and the Risk Guard result. A trade without a result, a cycle without a data package, a rejected check, and a test cycle. That a file is an attachment in UTF-8 that nothing stores, and that the account page and the dashboard lead to it. That a file holds nothing of another user, whatever its address names, and that asking writes nothing: the test is marked for the eight tables that are read. That a visitor is sent to sign in. That neither file holds a secret, in any form that bytes are written in. That a row of Demo stays labeled as Demo when its agent is switched to Live. That a name which starts as a formula is written as words. That more rows than one part come out once each, latest first. And what the log says. |
| `CsvTextTests` | The rule of a cell and of a line, with no database: quotes, the end of a line, what starts a formula, numbers under a culture that writes a comma, times, and names. |

`AgentReportTests` and `CycleRecordTests` in `tests/Ecnanib.Tests/DecisionEngine` hold what a trade and a cycle say, by the rules that the files share with the screen.

No test opens a file in a spreadsheet. By hand:

1. `docker compose up -d`, and run the application.
2. Sign in, and let an agent trade in Demo, or run a test cycle of it.
3. Open `/account`, and download both files.
4. Open each in a spreadsheet: the columns line up, an answer of a model is one cell, a loss is a number below zero, and every row says `Demo` or `Live`.

## Not built yet

- A choice of an agent, a mode, or a time. A file is of everything that its user has, and a spreadsheet filters it.
- The orders, the executions, and the funding of a position as files of their own, row by row. A trade has what they come to, and a user has each of them at Binance.
- Every rule of a check, with what it measured and what it was held to. A decision names the rules that its plan did not pass, and the page of the risk checks shows them all.
- The equity of an agent over time, the scored calls of its analysts, and the shadow trades of its vetoed plans. The screens show them: see [agent-screen.md](agent-screen.md) and [experience.md](experience.md).
- The tokens and the time of each call. The page of the AI usage adds them up (FR-RPT-3): see "AI usage" in [team.md](team.md).
- A limit to how often a user asks for a file.
