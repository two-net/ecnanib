# Agent screen

The agent screen shows: live team status, the current position and protective orders, an equity curve, the trade list, performance figures (return, win rate, average R, profit factor, largest drawdown, fees, funding), and the decision timeline with every role's output and the Risk Guard result (FR-RPT-2). A Demo or Live badge is always visible on it (FR-UI-3), and Demo results are never combined with Live results (FR-BIN-11).

It is the page of the state of an agent, `/agents/<id>/state`, which the dashboard, the list of the agents, and every other page of the agent lead to (FR-UI-2). So one screen says what the agent is doing now, how it has performed, and why it made each decision, beside the controls that start, pause, and resume it. Nothing of it is stored for the screen: every figure is worked out from the rows of its user each time the page is asked for.

The page is `AgentStatus.razor` in `src/Ecnanib/WebUi/Pages/Agents`, with its parts `AgentPerformance.razor`, `AgentEquityCurve.razor`, `AgentTradeList.razor`, `AgentDecisions.razor`, and `RoleCallAnswer.razor` beside it, and the texts of those parts in `AgentReportPages.cs`. What it reads is `IStoredAgents` and `IAgentControls` in `src/Ecnanib/Agents`, `IAgentReports` and `IRecordedCycles` in `src/Ecnanib/DecisionEngine`, `ICatalogModels` in `src/Ecnanib/Catalog`, and `INotifications` in `src/Ecnanib/Notifications`. What is written to it over its connection is `LiveFeed` in `src/Ecnanib/WebUi/Live`. The rules below are enforced by `AgentScreenPageTests` and `AgentModeBadgeTests` in `tests/Ecnanib.Tests/Agents`, by `AgentReportTests`, `EquityCurveTests`, and `CycleRecordTests` in `tests/Ecnanib.Tests/DecisionEngine`, and by `LiveFeedTests`, `LivePageTests`, `LiveListenerTests`, and `LiveRuleTests` in `tests/Ecnanib.Tests/LiveUpdates`.

What the state of an agent is, and its controls, are in [agents.md](agents.md). What the equity of an agent is, how it is recorded over time, and what the result of a position is, are in "The equity of an agent", "The equity of an agent over time", and "The results of a position" in [orders.md](orders.md). What a loss limit counts from is in "The loss limits" in [risk-guard.md](risk-guard.md), what a track record measures in "The measures" in [experience.md](experience.md), what a cycle records in [team.md](team.md), how a page follows what changes in [live-updates.md](live-updates.md), and whose rows a request reaches in [ownership.md](ownership.md).

## Per mode

| Rule | Detail |
|---|---|
| **The page has the one badge of its agent,** under its heading. | As every page of an agent has (FR-UI-3). `AgentModeBadgeTests` holds every route of the pages of an agent to it, with an agent that has a trade of the other mode. |
| **The figures and the curve are of the mode that the agent is in.** | The performance figures, and the equity as it was recorded, are those of the mode that the agent is in now, in the asset that its symbol is priced in. The page says so above the figures, and that nothing of the other mode is in them. An agent that was switched to Live has made nothing there, whatever it made in Demo. |
| **A trade and a decision say the mode that they were of.** | The trade list is every position that the agent closed, and the decisions are its latest cycles, in either mode. Each says the mode that the agent was in then, which stays as it was when the agent is switched. |
| **They say it as a word.** | "Demo" or "Live", and not with a badge: a badge of the other mode on the page of an agent would read as the mode of the agent. |
| **Nothing is added up over both modes.** | A trade of the other mode is on the list, and in no figure. |

## Live team status

Under the state of the agent, with since when and why, the page has a part "Team".

| Rule | Detail |
|---|---|
| **The four roles, each with the model that it was given.** | The Market Analyst, the Sentiment Analyst, the Strategist, and the Risk Manager, each with its model by the name that the catalog has for it, and the provider of the model: `claude-…, of Claude`, as the timeline of a cycle names the model of a call. A role that was given none says "No model chosen". |
| **What the team does now is written to the page as it changes.** | The steps of a cycle of the agent that runs, each as the cycle comes to it, over the connection of the page: see "The pages" in [live-updates.md](live-updates.md) and "The progress of a cycle" in [cycles.md](cycles.md). |
| **A team that does nothing says so.** | While no cycle of the agent runs, the part says "The team is at rest: no cycle of the agent runs at the moment." The state above it says whether the team is called at the next close. The page says so with `LiveShows.Team`. The other pages that show the cycles which run say nothing then. |
| **Nothing of what a model answered is there while a cycle runs.** | The decisions show that, once the cycle is recorded. |

## The current position and its protective orders

Under the team, the page says the open position of the agent, with the protection that rests on the exchange for it, the price that it is valued at, and what it would make at that price. It is written over the connection of the page as it changes, and was there before this screen: see "The pages" in [live-updates.md](live-updates.md) and "The position on a page" in [orders.md](orders.md). A stop-loss and a take-profit are said when an order for each rests on the exchange, and not otherwise: the price alone protects nothing.

## Performance figures

`IAgentReports` reads how one agent has performed, in one snapshot:

```csharp
public interface IAgentReports
{
    Task<AgentReport?> OfAgentAsync(Guid userId, Guid agentId, int tradesPage, CancellationToken cancellationToken);
}

public sealed record AgentReport(AgentEquity Equity, AgentFigures Figures, EquityCurve Curve, AgentTrades Trades);

public sealed record AgentFigures(
    decimal? NetResult, decimal? ReturnPercent, TrackRecord Record, int Closed, decimal Fees, bool FeesValued, decimal Funding);
```

| Figure | What it is |
|---|---|
| **Equity** | The capital that its user allocated to the agent, plus its net profit or loss to date, with what its open position would make now. |
| **Net result** | The equity, less the capital: what the agent made or lost to date, after fees and funding, with its sign. |
| **Return** | The net result in percent of the capital that the agent is allocated now, with its sign: `-0.82 percent`. |
| **Win rate** | The trades that made more than nothing, among the counted trades, in percent. |
| **Average R** | The mean result of a counted trade, in R, with its sign. |
| **Profit factor** | The R of the winning trades over the R of the losing ones, or "No losing trade". |
| **Largest drawdown** | The deepest fall of the recorded equity from its peak, in percent of the peak. See "The largest drawdown". |
| **Fees** | What Binance charged for the executions of the orders of the agent, as far as they are valued: a cost, written with no sign. |
| **Funding** | What its positions received as funding on the Futures market, less what they paid, with its sign. |

| Rule | Detail |
|---|---|
| **The equity is worked out as the dashboard works it out, by one rule.** | `AgentEquities.Valued`, from what `AgentResults.RowsAsync` says is settled of the result of the agent, with its open position at the price that the watch has: see "Total equity, and profit and loss for the day" in [dashboard.md](dashboard.md). So the screen of an agent and its card on the dashboard say the same equity. |
| **What is not known is said as not known, and never as a number.** | Where the watch has no price for the open position, the equity, the net result, and the return say "Not known at the moment", and the page says which symbol has no price. They are not what is settled of them. What is settled is said all the same: the fees, the funding, and the figures of the closed trades. |
| **The page never waits for Binance.** | It asks the exchange nothing. The feed of the page follows the price of the position, and the page asks for itself again when that price is first read: see "The page". |
| **The return is on the capital that the agent is allocated now.** | The specification names a return and does not define it. Agent equity is the capital plus the net result, so the return is the net result as a share of the capital. A capital that its user changed changes the return, and the page says the capital that it is a share of. |
| **The win rate, the average R, and the profit factor are those of a track record,** in R. | `TrackRecord.Of`, by the rule of the experience screens: see "The measures" in [experience.md](experience.md). They are of the positions that the agent closed in the mode that it is in. A trade counts once it has a result in R, and the page says how many trades the figures are of, how many closed trades are not counted yet, that a record is a small sample, and how many results are estimates. |
| **The fees and the funding are the sums that are in the equity.** | `AgentRows.Fees` and `AgentRows.Funding`, which `AgentResults.RowsAsync` reads with `ResultSums` for the equity (FR-BIN-10), each said on its own. The fee of an entry that opened nothing is among the fees. A fee that could not be valued is not, and the page says so. |
| **An amount is written as the dashboard writes it.** | To 2 decimal places, with the asset after it. A profit or a loss has its sign, so a loss is told from a profit without a colour (FR-THM-5). |

### The largest drawdown

```csharp
public sealed record EquityDrawdown(decimal Percent, decimal Amount, DateTimeOffset? PeakAt, DateTimeOffset LowAt);

public static class EquityCurves
{
    public static EquityCurve Of(IReadOnlyList<EquityPoint> recorded, int mostPoints = MostPoints);
    public static EquityDrawdown? LargestDrawdown(IReadOnlyList<EquityPoint> recorded);
}
```

| Rule | Detail |
|---|---|
| **It is counted as the drawdown limit counts a drawdown** (FR-RSK-8). | The fall of the equity from its peak, in percent of the peak, by `LossLimits.LossPercent`: the measure that the agent is halted on, so its user reads it against the limit that they set. The specification names a largest drawdown and does not say its unit. The drawdown in R of the experience screens is not on this page, so the page has one figure of that name. |
| **It is counted over the recorded equity,** every recording of it. | The rows of the agent in its mode and its asset, in the order of time. The peak is the highest result that was seen, and starts at the capital, which is a result of nothing: an agent that only lost fell from the capital that it began with, and the page says so. |
| **A capital that its user changed makes no drawdown, and takes none away.** | The peak is worked out anew for the capital of a recording, by `EquityMarks.Under`, as what watches the loss limits does it: the fall is the same share of the peak as before. |
| **The largest is the largest in percent.** | Two amounts under two capitals are not told which is the larger. The page says the fall as an amount as well, and from when to when it was. |
| **It differs from the limit in two things,** which the page does not hide. | The peak that the limit is counted from starts anew when the limit halts the agent, and this one never does: it is the largest fall that there was. And it is counted over the recordings, of which an agent that holds a position has one in a quarter of an hour at the most, so a fall between two of them is not in it. |
| **An equity that never fell from a peak has none.** | The figure says "None". |

## Equity curve

The equity of the agent over time is what the loss limits were evaluated on, as it is recorded with each evaluation: see "The equity of an agent over time" in [orders.md](orders.md).

```csharp
public sealed record EquityPoint(DateTimeOffset At, decimal Equity, decimal Capital);

public sealed record EquityCurve(
    int Recorded, IReadOnlyList<EquityPoint> Points,
    EquityPoint? First, EquityPoint? Latest, EquityPoint? Lowest, EquityPoint? Highest, EquityDrawdown? LargestDrawdown);
```

| Rule | Detail |
|---|---|
| **It is drawn on the server, as part of the page.** | An inline picture of two shapes: a line of the equity, and a dashed line at the capital of the agent. No script draws it, and no library is loaded for it. |
| **It carries no style of its own.** | The content security policy allows no style in a page: see [security.md](security.md). The shapes name their places, and their look is in `app.css`. |
| **It is in the colour of the text around it.** | So it reads the same on a light page and on a dark one (FR-THM-5). It has no colour for a profit and none for a loss. |
| **The line holds each value until the next.** | The equity is recorded when it changes, and once in a quarter of an hour while a price moves it. Between two recordings it is what the earlier one says. |
| **The capital is on the picture whatever the equity is.** | The line is read against it. The lowest and the highest place of the picture are the lowest and the highest of the equity and the capital. |
| **What the line shows is said in words under it.** | How many times the equity was recorded, where it began and where it is, and how low and how high it was, each with when. Whoever does not see the picture reads that. |
| **A long series is drawn with few points.** | `EquityCurves.Of` draws 240 at the most: the first, the lowest, the highest, and the last recording of each of the equal times that the series is cut into, so that no peak and no fall is lost from the line. The largest drawdown is counted over every recording, whatever is drawn. |
| **An equity that was recorded once has no line yet,** | and says its one value. One that was never recorded in the mode says so. |
| **Recordings in another asset say nothing of it.** | An agent that was given a symbol which is priced in another asset has its equity in that asset from then on, as its result has. |

## Trade list

The trades of an agent are the positions that it closed, the latest close first, 25 to a page:

```csharp
public sealed record AgentTrades(int Total, int Page, int Pages, IReadOnlyList<AgentTrade> Trades);

public sealed record AgentTrade(
    Guid PositionId, TradingMode Mode, Market Market, string Symbol, string Asset, PositionSide Side,
    decimal? Quantity, decimal EntryPrice, decimal? ExitPrice, DateTimeOffset OpenedAt, DateTimeOffset ClosedAt,
    ExitReason? ExitReason, AgentTradeResult? Result, bool Estimate);

public sealed record AgentTradeResult(decimal NetResult, decimal Fees, decimal Funding, decimal? ResultInR, TimeSpan Held);
```

| Rule | Detail |
|---|---|
| **A trade is a position that is closed.** | The open position is no trade yet: it is the position of the page. |
| **What it held is what was executed of its entry.** | A position has one entry. What the position holds grows less as it is closed in parts, so the position itself does not say how much it held. A position that no entry of the agent is stored for says "Not known". |
| **The exit price is the average of the orders that closed it,** by what each executed. | A stop-loss, a take-profit, and a close at the market, each at its average price. Where no order of the agent closed it, because it was closed at Binance, and where one of them says no price, the exit price says "Not known": an average of the others would be a price that looks right, and is not. |
| **Its result is the one that was recorded for it.** | The net result with its sign, the same in R, its fees, its funding, and how long it was held, from its row of `position_results`: see "The results of a position" in [orders.md](orders.md). A result is recorded some minutes after the close. Until then the trade says "Not recorded yet", and the page says why. |
| **An estimate says so.** | A result of which not all is what Binance said, and a position of which a part left the agent by no order of the agent. The page says what an estimate is. |
| **How it was closed is said in words.** | By its stop-loss, by its take-profit, by the team, by its user, by the service because its protection could not be placed or a loss limit was reached, or at Binance by no order of the agent. |
| **The pages are links.** | "Older trades" and "Newer trades", to `/agents/<id>/state?trades=<page>`. An address that names a page which there is not, such as a page after the last one, no whole number, or a page twice, is answered with "not found". |

## Decision timeline

The decisions of an agent are its latest cycles that a candle close started, the latest first, 5 at the most, each as a complete record. `IRecordedCycles` reads them:

```csharp
Task<IReadOnlyList<CycleRecord>> LatestRecordsAsync(Guid userId, Guid agentId, int count, CancellationToken cancellationToken);
```

| Rule | Detail |
|---|---|
| **A decision says the close of its candle, its mode, and how the cycle ended.** | The outcome in the words of the list of the cycles, with what it means: see "The outcome of a cycle" in [team.md](team.md). |
| **Every role's output.** | Each call of a role, in the order the calls were made: the role and its job, the model that was called and its provider, how the call ended, what was made of its answer, and the structured result that the model answered with, field by field. It is `RoleCallAnswer.razor`, which the timeline of a cycle shows as well: one piece of the page says it in both places. |
| **The Risk Guard result.** | Each check of the plan: what the plan does, the verdict, why, the size of the order that the check computed, and the outcome of every rule with its numbers (FR-RSK-9). It is `RiskCheckRules.razor`, as on the timeline and on the page of the risk checks. |
| **The orders that the decision led to, each as it is now,** | or why the cycle placed none. |
| **Each decision leads to the timeline of its cycle,** | which has the rest of its record: the summary of its data package, and the usage and the prompt templates of each call (FR-CYC-10). |
| **A test cycle is not among them.** | Its user ran it to see what the team would decide, and it placed no orders (FR-CYC-12), as on the dashboard. It is on the list of the cycles of the agent, which the part leads to. |
| **The records are read at one moment,** each table once for all of them. | `RecordedCycles` reads one cycle and several by the same lines, so a decision on the screen is what the timeline of its cycle says. |
| **What a model wrote is written as text,** | like everything else on a page (NFR-SEC-4). |

## The page

A signed-in user opens the screen of an agent at `/agents/<id>/state`. A visitor is sent to sign in.

| Rule | Detail |
|---|---|
| **It is rendered on the server for each request.** | As every page is. Its forms, which start, pause, and resume the agent and mark a notification as read, are posted with the token of the page: see [security.md](security.md). |
| **Everything is asked for by owner.** | Agents and Notifications take the request, and the Decision Engine, which may not use Accounts, is handed the user who is signed in. The page of an agent of another user is not found, like that of an agent that does not exist, whatever page of its trades is asked for: see [ownership.md](ownership.md). |
| **How the agent has performed is read once for a request.** | A form that changes nothing is answered on the page with why, and the page still shows the figures, the curve, the trades, and the decisions as they were read. |
| **It follows what changes** (FR-NTF-2). | When the agent, what it holds, a cycle of it that is recorded, its equity as it is recorded, or a notification of its user changes, the page asks for itself again: the subjects `Agent`, `Holdings`, `Cycle`, `Equity`, and `Notification`. So the trades, the figures, the curve, and the decisions are those of now without a manual refresh. |
| **The equity of an agent says when it is recorded.** | A trigger of `equity_snapshots` says a row that is added, with its agent: see "What the database says" in [live-updates.md](live-updates.md). The result of a position is recorded with a notification of its user, which the page follows, and the loss limits of its agent are evaluated after it, which records its equity. |
| **It asks for itself again when the price of its position is first read.** | The page was written without that price, or with an older one. The feed of the page follows the price of the position of the agent for the part that says it, and of no other agent: see "The feed of a page" in [live-updates.md](live-updates.md). |
| **A figure does not move between two changes.** | The position and its price are written over the connection as they change. The figures are of the moment that the page was written, and say the time of their price. |
| **It has no field to type in.** | A page that follows has none. It has buttons and links, and the pages of the trades are links. |
| **Its texts are in one place.** | `AgentReportPages` for the parts of this screen, with `AgentPages`, `CyclePages`, `RiskCheckPages`, `LivePages`, and the amounts of `DashboardPages` and the measures of `ExperiencePages` (NFR-USA-4). The times are UTC, and say so. |
| **The tables scroll by themselves on a narrow screen,** | also by keyboard, and the figures stand side by side as far as they fit (FR-UI-1). The look is in `app.css`: a page carries no style of its own. |

## Testing

| Test class | What it holds |
|---|---|
| `AgentScreenPageTests` | The page: every part of the screen from what an agent did, an agent that has done nothing, a figure that is not known and then is, that the figures and the curve are of one mode and that a trade and a decision of the other say so in words, the pages of the trades and a page that there is not, that nothing of another user is on it and that reading it writes nothing, and that it carries no style. |
| `AgentReportTests` | How an agent has performed, as it is read: the figures, the curve, and the trades, the mode of the agent, the price of an open position, what the orders of a trade say, the pages, by owner, and that nothing is written. |
| `EquityCurveTests` | What the recorded equity says, with no database: the largest drawdown, a capital that was changed, what of a long series is drawn, and numbers that cannot be worked out with. |
| `CycleRecordTests` | The latest cycles of an agent as complete records: the latest first, five at the most, no test cycle, by owner, and each as one cycle is read. |
| `LiveFeedTests`, `LivePageTests` | That the screen of an agent has the one price of its position followed and asks for itself again when that is first read, that an equity which is recorded has the page of its agent ask again, and that the page says when its team is at rest. |
| `LiveListenerTests`, `LiveRuleTests` | That the database says an equity that is recorded, with its agent, and that the trigger is the one of `LiveRules.cs`. |
| `AgentModeBadgeTests` | That the page has the badge of its agent, and no badge of the other mode. |

No test drives a browser. By hand:

1. `docker compose up -d`, and run the application.
2. Sign in, create an agent in Demo, start it, and open the page of its state: its team, its position, and the figures of an agent that has done nothing.
3. Once it has traded, look at the curve on a light and on a dark screen, and at the width of a phone. The console of the browser shows no report of the content security policy.
4. Add a row at the database, such as `INSERT INTO equity_snapshots (agent_id, user_id, mode, at, asset, capital, equity) VALUES (...)`: the curve shows it within two seconds, with no reload.

## Not built yet

- The team at work as an animated pipeline, with each role waiting, working, done, or failed (FR-ANI-1), and figures and a chart that animate (FR-ANI-2). The part of the team says the steps of a cycle as words.
- Figures that move with the price between two changes: the equity, the net result, and the return are of the moment that the page was written. The position and its price are written over the connection.
- A choice of the time that the curve is of. It is of all that was recorded in the mode. Until the equity over time is thinned to one row for a day after 90 days (NFR-DAT-5), all of it is read for each request.
- The fees, the funding, and the margin of the open position: see "Not built yet" in [orders.md](orders.md).
- The decisions before the latest five on this screen, and the cycles before the latest 50 on the list of the cycles.
- The export of the trades and the decisions as CSV (FR-RPT-4).
- The times of an agent in the time zone of its user (NFR-USA-3).
- The name of an agent in the list of the agents leads to its settings. The list has a link to this screen beside it.
