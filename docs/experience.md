# Experience

The system maintains a track record per provider and per model, filterable by role, market, timeframe, mode, and period. It shows closed trades, win rate, average R, total R, profit factor, largest drawdown in R, and average holding time (FR-EXP-2). Demo and Live experience are kept and scored separately, and what is not told a mode uses Live data where there is enough, otherwise Demo data, and says which was used (FR-EXP-9). Experience screens state that past results do not predict future results, that small samples are unreliable, and that Demo results differ from Live (FR-EXP-11).

A track record is of one user: of the closed trades of that user's agents. It is worked out from the recorded results of the closed positions each time it is asked for, and nothing of it is stored.

The code is in `src/Ecnanib/Experience`, what hands it the trades in `src/Ecnanib/DecisionEngine/ClosedPositions.cs`, and the screens in `src/Ecnanib/WebUi/Pages/Experience`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Experience` and `tests/Ecnanib.Tests/DecisionEngine/ClosedPositionTests.cs`.

What is recorded for a closed position is in "The results of a position" in [orders.md](orders.md). The catalog of models is in [catalog.md](catalog.md), whose screen links each model to its experience record. Whose rows a request reaches is in [ownership.md](ownership.md).

## The trades

Experience may not use the Decision Engine, which keeps the positions. So it declares what it needs of them, `IClosedTrades`, and the Decision Engine implements and registers it, as `ClosedPositions`. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IClosedTrades
{
    Task<IReadOnlyList<ClosedTrade>> OfUserAsync(Guid userId, DateTimeOffset? closedFrom, CancellationToken cancellationToken);
}
```

A `ClosedTrade` is one closed position: its mode, its market, its timeframe, when it was closed, its result, and the model that held each role when it was opened.

| Rule | Detail |
|---|---|
| **A trade is a position that is closed.** | `positions.closed_at` says so. An open position is no trade. |
| **A trade says what its position says.** | The mode, the market, and the timeframe are those of the agent when the position was opened, and the models are those of `position_roles`. What the agent is now, and which models its roles have now, says nothing of a trade that is over. So what an agent did in Demo stays Demo after it is switched to Live (FR-BIN-11). |
| **A trade may have no result yet.** | The result of a position is recorded some minutes after its close, and a day after it at the latest. Until then the trade has none, and says so. |
| **A role that had no model is not among the roles of a trade.** | Its position has no row for it. |
| **The trades are asked for by owner,** and read as they were at one moment. | `OwnedBy(userId)`, on the positions, their results, and their roles, in one snapshot. A trade of another user is not among them. |
| **A period is asked of the database.** | `closedFrom` leaves out what was closed before it, by `positions.closed_at`, itself included. The index `positions (user_id, closed_at)` serves the read, which the migration `AddClosedPositionIndex` added. |
| **A trade is kept nowhere else.** | The positions of an agent are deleted with it, and their results and their roles with them. So the trades of a deleted agent leave every record. The screens say so. |

## The measures

`TrackRecord.Of` works out the record of some trades. It is a rule only: it reads nothing.

| Measure | What it is |
|---|---|
| Closed trades | The trades that have a result in R. |
| Win rate | The trades of more than 0 R among them. A trade of exactly 0 R is neither a win nor a loss. |
| Average R, total R | The mean and the sum of the results in R. |
| Profit factor | The R of the winning trades over the R of the losing ones. It is not said for a record with no losing trade: there is nothing to divide by. It is 0 for a record with no winning trade. |
| Largest drawdown in R | The deepest fall of the running total of R from its highest point. The trades are in the order they were closed in, and the total starts at 0, which is its first height. It is written as the fall that it is: `3.2 R`. |
| Average holding time | The mean of `position_results.holding_seconds`. |

| Rule | Detail |
|---|---|
| **Every measure is in R.** | R is the amount that was risked on a trade. The trades of a record are of agents that are priced in different assets, whose amounts cannot be added up. A multiple of what was risked can. |
| **Every measure is of the same trades.** | A trade whose result is not recorded yet, and one whose result says no R, is in no measure. A view says how many of each it leaves out. |
| **A result that is an estimate counts,** and the record says how many of its results are estimates. | `position_results.complete` says which. A position that was liquidated, or that left its agent by no order of the agent, has such a result. Leaving those out would leave out the worst trades. |
| **A record of fewer trades than `Experience:MinSamples` is a small sample,** and is marked as one (FR-EXP-11). | 20 in the tracked file. |
| **The drawdown goes by the order that the trades were closed in,** however they are handed over. | Two trades of one moment are told apart by the ids of their positions, so the same trades always give the same record. |
| **Sums that are out of the range of a number are not said.** | A trade on which next to nothing was risked has a result of very many R. The measures that are sums are then "not available", and nothing fails. Nothing caps a result. |

## For whom a trade counts

`ITrackRecords` works out the records of a user, from the trades that `IClosedTrades` answers with. It is handed the user by id, as the Decision Engine is: Experience may not use Accounts.

```csharp
public interface ITrackRecords
{
    Task<TrackRecordView> OfUserAsync(Guid userId, TrackRecordFilter filter, CancellationToken cancellationToken);

    Task<ModelTrackRecordView> OfModelAsync(Guid userId, Guid modelId, TrackRecordFilter filter, CancellationToken cancellationToken);
}
```

A `TrackRecordFilter` is a role, a market, a timeframe, a mode, and a period. What is not chosen leaves nothing out.

| Rule | Detail |
|---|---|
| **With a role chosen, a trade counts for the model that held that role,** and for the provider of that model. | A trade whose team had no model in that role counts for nobody. |
| **With no role chosen, a trade counts once for every model of its team,** and once for every provider of that team. | A model that held two roles of a trade is counted once for it, and so is a provider that held several roles with one model or with more. |
| **The rows do not add up to the number of trades.** | A trade of four models counts in four rows. A record says how the teams with a model did, and not what the model did alone. The screens say so. |
| **A provider is the provider of the model.** | A model is of one provider, and a model of the catalog is never deleted (FR-CAT-5). So a role names its model alone, and the record of a model that its provider no longer offers stays. |
| **The record of a model is by role.** | `OfModelAsync` answers with the record of the model in any role, where a trade in which it held more than one counts once, and then in each role. The role of the filter is not gone by. |
| **A period reaches back from now.** | All time, or the last 7, 30, 90, or 365 days, each of 24 hours, by when a trade was closed. The moment that a period starts at is included. A drawdown within a period starts at 0 at the start of the period. |
| **The rows are in the order of the providers' codes, and then of the identifiers.** | Never in the order of a result: the screens rank nothing. |
| **Only a provider and a model that a counted trade counts for has a row.** | The page of a model that no trade of the user counts for says that instead. |

## Live or Demo

| Rule | Detail |
|---|---|
| **A record is of one mode.** | Nothing is added up over Demo and Live (FR-BIN-11). The filter has a choice of Demo and one of Live, and none of both. |
| **Where no mode is chosen, Live is used where there is enough, and otherwise Demo** (FR-EXP-9). | `ExperienceBasis.Choose` says which: Live when it has at least `Experience:MinSamples` samples, and Demo otherwise, also when neither has. It says whether the mode that it chose has enough. |
| **It says which was used.** | A view says its mode, whether the mode was chosen by the rule or asked for, and how many trades it would count in each. A screen shows the mode as a badge, says why it is that one, and leads to the other. |
| **What is enough is counted for the view,** and not for a row. | The trades that the view would count in a mode, under its other choices: its role, its market, its timeframe, and its period. A trade that is in no measure is no sample. |
| **The rule takes sample sizes, and not trades.** | So that whatever counts something else than trades goes by the same rule: the measures of the roles do (FR-EXP-3). |

A recommendation of a team is not built. It is to go by `ExperienceBasis.Choose`, with the sample sizes of what it recommends from: see "Not built yet".

## The screens

The pages are rendered on the server for each request, and neither is interactive. Each is for a signed-in user: a visitor is sent to sign in. The bar at the top of every page leads to the experience screen.

| Page | What it is |
|---|---|
| `/experience` | The experience screen (FR-UI-2): the track record of each provider, and of each model, from the closed trades of the user. Each model leads to its page, with the same filter. |
| `/experience/models/<id>` | The experience record of one model, which the catalog links each model to (FR-CAT-10): its track record in any role, and in each role. A role that the model never held says that no closed trade is counted, and no measure. |

| Rule | Detail |
|---|---|
| **The filter is in the address.** | `role`, `market`, `timeframe`, `mode`, and `period`, as `risk-manager`, `futures`, `4H`, `live`, and `90`. The form of the filter asks for the page by an address: it shows something and changes nothing, so an address can be kept and sent on. The page of a model has no choice of a role. |
| **A choice that is absent or empty is not made.** | The form sends every choice, also one that was left as it starts. |
| **An address that names a choice which there is not is not found.** | A value that is none of the choices, as they are written, and a choice that is made twice: `mode=both`, `mode=Live`, `market=1`, `period=31`. So a page never shows Demo in place of a mode that there is not, or every trade in place of a period that there is not. `ExperiencePages.FilterOf` reads the address, and matches each choice as it is written. |
| **The mode is under the heading, as a badge.** | With why it is that mode, that Demo and Live are never added up, and a link to the other mode, which says how many trades it would count there. The other mode is named as a word: a page has one badge. |
| **A result in R says its sign.** | `+1.25 R`, `-0.5 R`, `0 R`, with two decimal places at the most. So a profit is told from a loss without a colour (FR-THM-5). A win rate is written in percent, as a word. |
| **A holding time is written in its two largest units.** | `2 days 4 hours`, `3 hours 20 minutes`, `45 minutes`. |
| **What a record cannot say is "Not available".** | And the profit factor of a record with no losing trade is "No losing trade". No cell is left blank. |
| **What is left out is said.** | How many closed trades are not counted yet because their result is not recorded, and how many are not counted because their result says no R, each as a note. |
| **The page of a model that no trade of the user counts for is there.** | It says that nothing is recorded for the user in that mode. The catalog links every model to its page, so it is not "not found". A path that names no model of the catalog is. |
| **The records are asked for by owner.** | A page hands `ITrackRecords` the user who is signed in. The catalog is the same for everyone, and the page of a model has one address for every user: each sees the record of their own trades there. |
| **What a provider wrote is shown as text.** | The name of a model, like everything else on a page (NFR-SEC-4). |
| The times are UTC. | As on the other pages. |

The texts of the screens are in `ExperiencePages`, in one place, with how a filter is named in an address and read from one.

### What every screen states

Every experience screen states, under the heading "What these figures cannot tell" (FR-EXP-11):

- that past results do not predict future results,
- that small samples are unreliable, with the sample size that a record is marked below,
- and that Demo results differ from Live results.

`ExperienceNotes` states all three, and asks for nothing of the page that shows it. So a page states them whatever it has to show, also when it has no trade to show. `ExperienceStatementTests` asks every route of `Ecnanib.WebUi.Pages.Experience`, with no trade, with trades in Demo, and with trades in both modes, so a screen that is added fails it until it makes the statements.

## The settings

| Key | What it is | In the tracked file |
|---|---|---|
| `Experience:MinSamples` | The sample size below which a record is marked as a small sample, and from which Live data is enough to go by. It is also the sample size below which a slice shows "not enough data" (FR-EXP-4), which is not built. | 20 |

The other keys of `Experience` are for what is not built: `LessonsPerPrompt`, `Damping`, and those of `Board`. The file is read with them, and nothing uses them yet. How a setting reaches a component is in [configuration.md](configuration.md).

## The data

Experience has no table. A track record is worked out from `positions`, `position_results`, and `position_roles`, which the Decision Engine keeps: see "The record of an order" in [orders.md](orders.md). So nothing of it is to be deleted with an account or with an agent, nothing of it is in the export of a user's data beyond what those tables are, and it cannot say something else than the positions say.

## Testing

`TrackRecordTests` and `ExperienceBasisTests` hold the measures and the rule of Live or Demo to the tables above, without a database. `ExperienceTextTests` does so for how a record is written and how a filter is named in an address.

`ClosedPositionTests` reads the trades as Experience does, through `IClosedTrades`, and has the test with two users, which is marked for `positions`, `position_results`, and `position_roles`. `TrackRecordsTests` asks `ITrackRecords`, and `ExperiencePageTests` reads the screens as a signed-in user does.

`StoredTradeRows.GiveTradeAsync` gives an agent a closed position with its roles and its result, stored as the application stores them, without the cycle and the orders that lead to one. A test says of it what it is about: its result in R, its mode, its market, its timeframe, whether its result is complete, and whether it is recorded at all. Most of these tests take 3 trades as the minimum sample, with a file of their own, so that a test has enough of them with a few.

## Not built yet

- The measure of each role, the strength score of a model in a role, and the shadow trades of vetoed plans (FR-EXP-3, FR-EXP-4, FR-EXP-5). The track record is of closed trades alone.
- Recommending a team by measured strength (FR-TEAM-4). It is to use Live data where there is enough, otherwise Demo data, and to say which (FR-EXP-9): `ExperienceBasis.Choose` is that rule, and the experience screens go by it already.
- Lessons: writing one after each closed position, recalling them into prompts, and the page that a user reads and manages them on (FR-EXP-6, FR-EXP-7, FR-EXP-8).
- The platform-wide board, which shows the same track record aggregated across all users (FR-EXP-10). A track record is of one user.
- Keeping the trades of an agent that is deleted. The specification keeps positions indefinitely (NFR-DAT-5), and deleting an agent deletes its positions: see "The record of an order" in [orders.md](orders.md). A track record follows the positions, so it loses those trades.
- A track record by symbol, by agent, or by exit reason. A trade says none of them to Experience.
- Times in the user's own time zone (NFR-USA-3). The pages write a time through `PageTimes`, in UTC.
