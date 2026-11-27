# Experience

The system maintains a track record per provider and per model, filterable by role, market, timeframe, mode, and period. It shows closed trades, win rate, average R, total R, profit factor, largest drawdown in R, and average holding time (FR-EXP-2). Demo and Live experience are kept and scored separately, and what is not told a mode uses Live data where there is enough, otherwise Demo data, and says which was used (FR-EXP-9). Experience screens state that past results do not predict future results, that small samples are unreliable, and that Demo results differ from Live (FR-EXP-11).

A role-specific measure is kept for each role: for the two analysts the share of their calls that matched the price change over the next 4 closed candles, for the Strategist the average R of its executed plans, and for the Risk Manager the average R of what it approved minus the average R that what it vetoed would have produced (FR-EXP-3). A strength score per model, role, market, and timeframe is that measure damped toward zero for small samples, and the screens show it with its formula (FR-EXP-4). Vetoed plans are evaluated as shadow trades against later closed candles, and never place orders (FR-EXP-5).

After each closed position, the agent's Risk Manager model writes a short lesson: the setup, what was expected, what happened, and one takeaway of at most 60 words (FR-EXP-6). Each Strategist and Risk Manager prompt includes up to 5 recent lessons, taken first from the same agent and then from the user's other agents on the same symbol, market, and timeframe, within a fixed size budget (FR-EXP-7). A user reads all of their lessons, disables or deletes any lesson, and resets an agent's lessons (FR-EXP-8). A disabled or deleted lesson is not recalled into prompts, and lessons are recalled only from the same user's agents.

A platform-wide board shows the same track record aggregated across all users. A slice is shown only when at least 5 users and 50 trades contribute. It exposes no user identity, balance, or individual trade, and users can opt out of contributing (FR-EXP-10).

A track record is of one user: of the closed trades of that user's agents. It is worked out from the recorded results of the closed positions each time it is asked for, and nothing of it is stored. A strength score is worked out in the same way, and no aggregate is stored either. The board is worked out in the same way too, from the closed trades of every user who has not left it. What Experience stores is what later candles are to say something of: the calls of the analysts and the shadow trades of the vetoed plans, each of which a job gives its outcome. And the lessons, each of which a job of the Decision Engine has written. And who left the board, which is each user's own choice.

The code is in `src/Ecnanib/Experience`, what hands it the trades in `src/Ecnanib/DecisionEngine/ClosedPositions.cs`, what leaves it the calls and the vetoed plans of a cycle in `src/Ecnanib/DecisionEngine/CycleExperience.cs`, and the screens in `src/Ecnanib/WebUi/Pages/Experience`. What has a lesson written is in `LessonWriting.cs` and `LessonRuns.cs` of `src/Ecnanib/DecisionEngine`, and what hands a cycle its lessons in `RecalledLessons.cs` there. The rules below are enforced by tests in `tests/Ecnanib.Tests/Experience`, and in `ClosedPositionTests.cs`, `CycleExperienceTests.cs`, `LessonWritingJobTests.cs`, `LessonPromptTests.cs`, and `CycleLessonTests.cs` of `tests/Ecnanib.Tests/DecisionEngine`.

What is recorded for a closed position is in "The results of a position" in [orders.md](orders.md), and what is recorded for a cycle in "The record of a cycle" in [team.md](team.md). The candles are those of the Exchange Gateway: see "Market data" in [exchange.md](exchange.md). The catalog of models is in [catalog.md](catalog.md), whose screen links each model to its experience record. Whose rows a request reaches is in [ownership.md](ownership.md), and how a job runs in [background-work.md](background-work.md).

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

What recommends a team goes by the same rule, with the sample sizes of the slices that it recommends from, which `IRoleStrengths` answers for each mode: `RoleRecommendations` of Agents chooses the mode for each model, from the samples of its slice in Live and in Demo, and says which it used (FR-TEAM-4). See "The strength score", and "Role by strength" in [agents.md](agents.md).

## The platform-wide board

`IBoardRecords` works out the board: the same track record, of each provider and of each model, from the closed trades of every user who has not left the board (FR-EXP-10). It takes no user, and answers with none.

```csharp
public interface IBoardRecords
{
    Task<BoardView> OfEveryoneAsync(TrackRecordFilter filter, CancellationToken cancellationToken);
}
```

A `BoardView` is the records of the providers and of the models in one mode, as `ProviderTrackRecord` and `ModelTrackRecord`, which are the rows of a user's own view. With them it says what the view is of: its mode, whether the rule chose it, from when a trade counts, and how many users and trades must contribute.

| Rule | Detail |
|---|---|
| **It is the same track record.** | The filter is the one of a user's records: role, market, timeframe, mode, and period. For whom a trade counts is one rule, `CountedTrades`, which both go by, and the measures are those of `TrackRecord.Of`. `BoardRecordsTests` holds a record of the board to what `TrackRecord.Of` says of the same trades. |
| **A slice is one record:** that of a provider, or that of a model, of the trades that the filter counts, in one mode. | Each is held to the rule by itself. A provider can have a record while none of its models has one, and a choice of the filter can leave a record too few. |
| **A slice is shown only when at least `Experience:Board:MinUsers` users and `Experience:Board:MinTrades` trades contribute to it.** | 5 and 50 in the tracked file. `BoardSlice.IsShown` is the rule, and reads nothing. `BoardSliceTests` holds it to those numbers. |
| **A trade contributes when it is counted,** and a user when a trade of theirs is. | A trade with a result in R. A user is counted once, however many of the trades are theirs: one user with many trades shows nothing, and neither do many users with a few. |
| **A slice that is not shown is in no answer.** | It is not worked out, it has no row, and nothing says how many trades it would count, or that there is one. |
| **An answer names no user.** | A `BoardView` holds no user, no agent, no position, no symbol, and no amount of money: the measures are in R, and the one id in it is that of a model of the catalog. `BoardRecordsTests` reads the types of the answer for it. |
| **A record is of one mode.** | Nothing is added up over Demo and Live (FR-BIN-11). Trades that are enough only when both modes are counted together show nothing. |
| **Where no mode is chosen, Live is used where it has a record to show, and otherwise Demo** (FR-EXP-9). | What is enough for the board is one record that may be shown: `ExperienceBasis.Choose` is asked with how many providers each mode shows, and with 1 as the minimum. The trades of a model are among those of its provider, so a mode with no provider to show has no model to show either. The choice says nothing that asking for each mode does not say. The view says its mode and whether the rule chose it, and no number of trades. |
| **Nothing of it is stored.** | It is worked out from the positions each time it is asked for, as the record of a user is. So a user who leaves the board is in no record from the next time on, and neither are the trades of an agent or of an account that is deleted. See "Not built yet". |
| **The rows are in the order of the providers' codes, and then of the identifiers.** | Never in the order of a result: the board ranks nothing. |

**What the rule does not keep from a reader** is what two records that are both shown say together. A provider whose record counts 51 trades, beside its one model that is shown with 50, says the result of one trade. So do the same record with and without a choice of the filter, and the same record a minute later. Such a difference is still a result in R with no user, no agent, no symbol, and no amount to it. Holding it back needs a second rule, which hides a record for what another shows: see "Not built yet".

### The trades of the board

Experience declares what it needs of whatever keeps the positions, `IBoardTrades`, and the Decision Engine implements and registers it, with `ClosedPositions`, as it does `IClosedTrades`.

```csharp
public interface IBoardTrades
{
    Task<IReadOnlyList<ContributedTrade>> OfContributorsAsync(DateTimeOffset? closedFrom, CancellationToken cancellationToken);
}

public sealed record ContributedTrade(Guid UserId, ClosedTrade Trade);
```

| Rule | Detail |
|---|---|
| **It is a question of its own.** | `IClosedTrades` answers with the trades of one user, and never with more. What reads across users has another name, so that nothing which asks for the trades of a user comes by those of another. Only `BoardRecords` asks it. |
| **It is not asked for by owner.** | It is the one read for a page that is not: see "What is not asked for by owner" in [ownership.md](ownership.md). It says whose each trade is, by id, because the board counts the users of a slice. The id is in no answer of the board. |
| **A user who left the board is left out where the positions are read.** | The question leaves out the positions of every user who has a row of `board_opt_outs`, in the database. So a trade of such a user is handed to nothing. |
| **Only a closed position with a result in R is read.** | The board counts no other, and says nothing of one: it has no note of the trades that wait for their result. |
| **It is read as it was at one moment,** with who left the board. | One snapshot, as for the trades of a user. A user who leaves while it reads is in all of the answer, or in none of it. |
| **A period is asked of the database.** | `closedFrom`, as for the trades of a user. No index serves the read across users: see "Not built yet". |

### Leaving the board

A user can opt out of contributing (FR-EXP-10). `IBoardContribution` answers whether the trades of a user are counted, and stores the choice. It is handed the user by id, as `ILessons` is.

```csharp
public interface IBoardContribution
{
    Task<bool> OfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SetAsync(Guid userId, bool contributes, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **Every user contributes until they leave.** | Nothing is stored for a user who contributes. A user who left has a row of `board_opt_outs`, with when they left, and a user who comes back has none again. |
| **It holds at once.** | The board is worked out each time it is asked for. Every trade of a user who left is left out, also the ones from before they left, and every one counts again when they come back. |
| **It changes nothing else.** | The user still sees the board, and their own records, strength scores, and lessons are as they were. |
| **A choice that is made twice changes nothing the second time.** | Of two forms that are sent at the same time, the second writes nothing: the row is written with `ON CONFLICT DO NOTHING`. Leaving again keeps when the user left. |
| **It is the user's own.** | The row is asked for with `OwnedBy`, and the statement that writes it names the owner itself. `BoardRecordsTests` has the test with two users, which is marked for `board_opt_outs`. |
| **It is a preference of the user,** the first that was stored. | It is in the export of the user's data, and goes with the account: see "The data". It is in no event of the audit trail: it exposes no key and moves no money. |
| **It takes no lock on the account.** | Nothing is decided from what the account is, so nothing has to hold it still. A user whose account is deleted in the moment that they leave the board has that one request fail: the key to the account refuses the row, and none is left behind. |

## The measure of each role

`RoleMeasures` works out the measure of a model in a role (FR-EXP-3). It is a rule only: it reads nothing. A `RoleMeasure` is the measure, or nothing where it cannot be said, and its sample size.

| Role | The measure | A sample |
|---|---|---|
| Market Analyst, Sentiment Analyst | The share of the calls that were not neutral whose direction matched the price change over the next 4 closed candles, minus 50 percent. It is a fraction from -0.5 to 0.5, and a model that is right half of the time is at 0, where a coin is. | A call that was scored: one that matched, or one that did not. |
| Strategist | The average R of the plans that were executed. | A closed trade with a result in R, for the model that was the Strategist of its position. |
| Risk Manager | The average R of the plans that it approved, minus the average R that the plans which it vetoed would have produced. One that vetoes the plans which would have lost is above 0. | A plan that it reviewed and that has a result, on either side: a closed trade for the model that was the Risk Manager of its position, or a shadow trade with a result for the model that vetoed its plan. |

| Rule | Detail |
|---|---|
| **A neutral call is no sample.** | It called no direction, so nothing is scored, and no row is written for it. |
| **An executed plan is a closed trade.** | A plan that opened a position is the trade of that position, so the Strategist and the approved side of the Risk Manager are worked out from the trades of `IClosedTrades`, by the model that `position_roles` names for the role. A plan that closed a position, or moved its protection, has no result of its own. |
| **A plan whose risk the Risk Manager reduced is approved.** | It went on, with less risk, and a result in R is a multiple of what was risked. |
| **The measure of the Risk Manager is said only where both sides have a result.** | Without a vetoed plan there is nothing to take away, and what is left would be the measure of the Strategist. Without an approved one there is nothing to take it from. The sample is counted all the same, and the slice says what it is made of: `6 (4 approved, 2 vetoed)`. |
| **A result that is an estimate counts,** as in a track record. | Leaving out the positions that were liquidated would leave out the worst plans. |
| **Results that are out of the range of a number say no measure.** | A trade on which next to nothing was risked has a result of very many R. The sample is counted, the measure is "Not available", and nothing fails. |
| **A measure is of one mode.** | Whoever asks hands over the samples of one mode (FR-EXP-9, FR-BIN-11). |

## The strength score

`StrengthScore.Of` is the formula of the specification (FR-EXP-4):

```
strength score = measure × n ÷ (n + damping)
```

where n is the sample size of the measure, and the damping is `Experience:Damping`, which is 20 in the tracked file. A measure of 20 samples counts for half of itself, one of 180 for nine tenths, and one of 5 for a fifth: a few lucky results do not look like strength. `StrengthScoreTests` holds the formula.

| Rule | Detail |
|---|---|
| **A score keeps the sign of its measure,** and is never further from 0 than the measure is. | It is in the unit of its measure: a share for an analyst, and R for the Strategist and the Risk Manager. |
| **A slice of fewer samples than `Experience:MinSamples` has no score.** | It says its sample, and "Not enough data". With no score, nothing can recommend from it (FR-EXP-4). `RoleStrength.Of` decides it, in one place. |
| **A slice whose measure cannot be said has no score either,** whatever its sample. | A Risk Manager that has results on one side only. |
| **No sample and no damping divide nothing.** | The score is 0. The file may set the damping to 0, and then a score is its measure. |
| **A score is in the range of a number wherever its measure is.** | It is worked out as the formula is written, and where the measure cannot be multiplied by its sample, as the measure times the sample over the sample and the damping, which is 1 at the most. |

`IRoleStrengths` answers the slices of a user. It is handed the user by id, as `ITrackRecords` is.

```csharp
public interface IRoleStrengths
{
    Task<RoleStrengthView> OfUserAsync(Guid userId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken);

    Task<RoleStrengthView> OfModelAsync(
        Guid userId, Guid modelId, TrackRecordFilter filter, TradingMode mode, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **A slice is of one model, one role, one market, and one timeframe.** | Nothing is added up over two of any of them. A model that was the Market Analyst of an agent on Spot on the hour and of one on Futures on 4 hours has two slices. |
| **It takes its mode, and chooses none.** | A slice is of one mode. A screen hands over the mode that it shows, which its track records say. What recommends from the scores asks for each mode, and goes by `ExperienceBasis.Choose` with the sample sizes of the slices: Agents does, for the form of an agent and for the automatic reassignment of its roles (FR-TEAM-4, FR-TEAM-10). The mode of the filter is not gone by. |
| **The filter chooses among the slices.** | A role, a market, and a timeframe each leave the other slices out. `OfModelAsync` answers every role of one model, and does not go by the role of the filter. |
| **A period counts a sample by when it happened.** | A call and a vetoed plan by the close of the candle of their cycle, and an executed plan by when its position was closed, as a track record does. |
| **Only a slice with a sample is answered.** | A user with nothing has no slice. |
| **The slices are in the order of the providers' codes, the identifiers, the roles, the markets, and the timeframes.** | Never in the order of a score: the screens rank nothing. |
| **No aggregate is stored.** | The calls are counted by the database, by slice and outcome, because an agent of a short timeframe makes one every quarter hour. The trades and the shadow trades are handed over as they are. |
| **What is left out is counted.** | The calls that are not scored yet, the calls that could not be scored, the shadow trades that have not ended, and the ones that ended with no result. A screen says each. |

## What a cycle leaves for Experience

The record of a cycle says its market and its timeframe in the summary of its data package alone, which is one line of JSON, and a vetoed plan in the answer of its Strategist alone. And the exchange gives the candles of the recent past only. So what later candles are to say something of is written out as rows of Experience when the cycle is recorded, in the same transaction, and a job completes them: see "The job".

`CycleExperience.Of` of the Decision Engine makes the rows from the record of a cycle, and `CycleRecords.StoreAsync` stores them with it. Experience may not use the Decision Engine, so it is the Decision Engine that hands the facts over, to `AnalystCall.Of` and `ShadowTrade.Of`, which decide what a row says.

| Rule | Detail |
|---|---|
| **Only a cycle that a candle close started leaves anything.** | A test cycle places no order, and its user runs as many of them for one candle as they like: it would be the same call, counted again (FR-CYC-12). |
| **A cycle with no summary of its data package leaves nothing.** | One that ended before its package was built says no market, no timeframe, and no price. |
| **A call is left for each analyst that gave a valid answer and called a direction,** however the cycle ended. | The analyst called what it called, whatever the team made of it afterwards: a plan that held, a decision that was stale, a role that gave no decision, or a veto. |
| **A shadow trade is left for a cycle that ended as `Vetoed`.** | With the entry, the stop-loss, and the take-profit of the plan, and the model of the Risk Manager's call. The veto is the outcome of the cycle, and not the answer of the Risk Manager, which is not always kept. |
| **The latest valid answer of a role is the one that is read.** | An answer that was rejected is no call. |
| **A plan that reverses is on the other side of what the agent held,** which the summary of the data package says. | A plan names no side for a reverse. |
| **It is made from the record, and from nothing else.** | The row of the cycle and the calls of its roles, as they are stored. What stores a cycle is handed nothing more for it. |
| **It fails nothing.** | What cannot be read leaves no row, and the cycle is stored all the same: the record of a cycle is not given up for what is worked out from it. |
| **A row says what its cycle says, in columns of its own.** | The mode, the market, the symbol, and the timeframe of the agent when the cycle ran. What an agent did in Demo stays Demo (FR-BIN-11), and no summary is read to work out a measure. |

## The calls of the analysts

An `AnalystCall` is the direction that the Market Analyst or the Sentiment Analyst called in a cycle, with the close of the candle that the cycle was for. `CallScore.Of` says whether it matched.

| Rule | Detail |
|---|---|
| **The price change is from the close of the cycle's candle to the close of the 4th candle after it.** | By the candles of the agent's own timeframe, market, symbol, and environment: a call in Demo is scored against the candles of Binance's test environment. |
| **A bullish call matches a higher close, and a bearish call a lower one.** | What the candles between them did says nothing. |
| **The 4th candle is counted, and not timed.** | Where Binance has no candle for a time, during a halt, the candles after a call are the ones that there are, and the 4th of them is the one. |
| **A price that did not change matches no direction.** | The call said that it would change, so it is a sample, and one that was not right. |
| **A call is due when its 4th later candle can have closed.** | An hour after a call on 15 minutes, and 4 days after one on a day. Until then nothing asks the exchange about it. |
| **A call is scored once.** | Its row has its outcome, the later price, and when it was scored, and nothing changes it after that. |
| **The Sentiment Analyst is scored whether its model searched live or not.** | A call of a model that cannot search is the call of that model in that role. |

## Shadow trades

A `ShadowTrade` is what a vetoed plan would have produced. `ShadowTradeRule.Of` follows it over the candles that closed after the plan, with its own entry, stop-loss, and take-profit and with standard fees (FR-EXP-5). It is a rule only: it is handed prices and candles, and answers with a result.

| Rule | Detail |
|---|---|
| **A shadow trade never places an order.** | It is a row, and a rule that reads candles. Nothing of Experience can reach the orders of the Exchange Gateway: `ComponentRules.cs` opens `ExchangeGateway.Trading` to the gateway and to the Decision Engine alone, `DependencyRuleTests` holds every type to it, and `ExperienceModelTests` pins it for this. |
| **The stop-loss is taken to be hit first when it and the take-profit fall inside one candle.** | A candle does not say in which order it reached its prices, and the worse of the two is assumed. |
| **An entry at the market is entered at the close of the candle that the plan was made on.** | Which is the price that the plan was written on. So every later candle counts, whole. |
| **An entry at a price is entered at that price, once the first later candle reaches it,** and not at all otherwise. | An entry that rests is taken off when the next candle of its agent has closed: see "An entry at a price" in [orders.md](orders.md). One that was not entered ends as `NotEntered`, with no result. |
| **In the candle that enters an entry at a price only the stop-loss counts.** | The candle does not say whether its other end came before the entry. A candle that came to the stop-loss came through the entry first. |
| **It ends at its own prices.** | A candle that opens beyond the stop-loss still ends the trade at the stop-loss. |
| **It is followed for 200 later candles, and then closed at the last close.** | `ShadowTradeRule.CandlesFollowed`. Nothing manages a shadow trade as a team manages a position, and the exchange gives only the newest 498 candles of a length, so one that has reached neither price by then ends as `TimeUp`, with the result at that close. That is about 2 days on 15 minutes, and 200 days on a day. |
| **The result is in R:** what its price moved by, less the fee on its entry and on its exit, over the distance from its entry to its stop-loss. | The quantity is on both sides, and so in neither: a shadow trade has no size. It is rounded to 12 decimal places, as the result of a position is. |
| **The standard fee is a setting, for each market.** | `Experience:ShadowFeePercent`, in percent of what an order is worth. The tracked file has what Binance charges an account with no discount for an order that takes: 0.1 on Spot and 0.05 on Futures. It is charged twice, for the entry and for the exit. |
| **No funding is counted.** | See "Not built yet". |
| **A plan whose stop-loss or take-profit is on the wrong side of its entry has ended as it is written,** as `Rejected`, with no result. | Nothing asks where they stand before the veto: the Risk Guard checks what goes on, and a vetoed plan does not. It would have rejected such a plan (FR-RSK-4), so no trade would have been made of it. No other rule of the Risk Guard is asked: its facts are those of the moment. |
| **A result that is out of the range of a number is not said.** | A stop-loss next to the entry risks next to nothing. The trade ends as it ended, with no result. |
| **Only a shadow trade with a result is a sample.** | One that is open still, one that was not entered, one that was rejected, and one whose candles could not be had is in no measure, and a screen says how many there are. |

## The job

`ExperienceScoringJob`, named `experience-scoring`, scores the calls and follows the shadow trades every 5 minutes. `ExperienceScoring` does the work.

| Rule | Detail |
|---|---|
| **It runs for the rows that are due, and for no other.** | A call is due when its 4th later candle can have closed, and a shadow trade when the next candle has that it was not followed over. A run that finds none asks the exchange nothing. A candle closes every quarter hour at the soonest. |
| **It takes the 200 rows of each kind that are due the longest.** | `ExperienceScoring.Most`. A shadow trade that is open still is given the close of the next candle as when it is due, so the rows that wait behind it have their turn. |
| **The candles of a symbol are read once for everything that needs them.** | One read for each market, environment, symbol, and timeframe, through `IExchangeMarketData`, where a read is shared with whatever else asks: see "Market data" in [exchange.md](exchange.md). They need no key. |
| **A row is completed once.** | An update changes a row only while it has no outcome. So a row that went with its agent while the candles were read fails nothing, and a row that has its outcome keeps it. |
| **A shadow trade is followed anew from its close with every look.** | Its row keeps no progress but when it is next due: the rule is handed every candle after the plan, which the exchange still gives for as long as a shadow trade is followed. |
| **A read that fails leaves everything for the next run.** | The exchange gave no answer, or the limiter held the call back. Nothing is written, and the log says so. |
| **A candle that has not closed yet is waited for.** | The exchange may not have begun the next candle when a row is due. The row is due still with the next run. |
| **Candles that are no longer given end a row with no result,** as `NoCandles`. | The oldest candle that the exchange gives began after the cycle's own had closed: the service was down for longer than the candles reach back, which is about 5 days on 15 minutes. Or the configuration file no longer lists the symbol, which the gateway answers with an error. Or the exchange gives no candle of the symbol at all, and the cycle is longer ago than 498 candles last: until then such a row waits. |
| **It works across users.** | A job has no user who is signed in. Each row says whose it is, and what is written of a row is written to that row alone: see "What is not asked for by owner" in [ownership.md](ownership.md). |
| **No test application runs it on a schedule.** | A test runs it itself, from the key `EcnanibApplicationFactory.ExperienceScoringKey`: see [background-work.md](background-work.md). |

## Lessons

A `Lesson` is what one closed position taught, as a row of `lessons`: the setup, what was expected, what happened, and one takeaway (FR-EXP-6), with what its position says, and with whether it is recalled. `LessonWords` is what a lesson says, and `LessonLimits` what it is held to. Both are rules only: they read nothing.

| Rule | Detail |
|---|---|
| **A position has one lesson.** | The row is found by its position: `position_id` is its key. So a lesson is named by the id of its position, on a page and in an address. |
| **It is written with the result of its position, and completed once.** | What records the result of a closed position writes the row with it, with nothing of a lesson yet: see "Writing a lesson". A job gives the row what its model wrote, or says why it has nothing, and nothing completes it a second time. |
| **A lesson has four parts, each on one line.** | `LessonWords.OneLine` makes every run of white space, line breaks, and characters that are not written one space. So no part of a lesson begins a line, and a lesson cannot pose as a heading of the prompt that recalls it. |
| **It is handed to a prompt as one line:** `Setup: … Expected: … What happened: … Takeaway: …`. | `LessonWords.TextOf`. The names of the parts are the service's own. |
| **The takeaway has at most 60 words** (FR-EXP-6). | `LessonLimits.TakeawayWords`. A word is what stands between white space. It has at most 600 characters as well: a word can be long. |
| **The setup, what was expected, and what happened have at most 500 characters each.** | `LessonLimits.PartCharacters`. The instructions ask for one sentence each. |
| **The limits are fixed, and are no settings.** | The size of what a prompt recalls follows from them: see "Recalling lessons". |
| **A lesson has at most 3 tags, of a fixed set.** | `LessonTag`: entry, stop-loss, take-profit, risk, trend, range, volatility, sentiment, funding, fees, and timing. The model chooses from the set, which the schema of a lesson lists, so a tag is never text of a model's own. A row keeps them by their names, in the order of the set. |
| **A lesson says what its position says, in columns of its own,** and names its agent. | The agent, the mode, the market, the symbol, and the timeframe of the position, when it was closed, and its result in R. Experience cannot read the positions, and what recalls lessons and what resets those of an agent go by these columns. |
| **A lesson says its mode, and is recalled whatever it is.** | What an agent learned in Demo stays its lesson after it is switched to Live. A page says the mode of each lesson, as a word (FR-BIN-11). |
| **Only a lesson that is written and enabled is recalled.** | Its user disables it, and enables it again, which changes nothing else of the row. A new lesson is enabled. |
| **A lesson goes with its position,** which goes with its agent. | The lessons of a deleted agent leave every prompt and every page: see "Not built yet". |

What became of the lesson of a position is its outcome, which is nothing while it waits:

| Outcome | What it says |
|---|---|
| `Written` | The lesson is written. |
| `NoModel` | The Risk Manager of the agent had no model when the lesson was due, so none was called. |
| `NoKey` | The user of the agent had no key that could be used for the provider of that model, so none was called. |
| `NotComposed` | The prompt of the call was not composed: a part of it held what a prompt never holds (FR-PRM-9). |
| `NoResult` | The model was called, once more where that is done, and gave no valid lesson. |

## Writing a lesson

`LessonWritingJob`, named `lesson-writing`, has the lessons written every 5 minutes. `LessonWriting` does the work, and `LessonRuns` calls the model. They are of the Decision Engine, and not of Experience, which keeps the lessons: only the Decision Engine calls a model, with a prompt that it composed (NFR-SEC-8). Experience declares what the job asks and completes, `IDueLessons`, and implements it itself.

```csharp
public interface IDueLessons
{
    Task<IReadOnlyList<DueLesson>> DueAsync(int most, CancellationToken cancellationToken);

    Task<bool> ClaimAsync(Guid positionId, CancellationToken cancellationToken);

    Task<bool> WriteAsync(Guid positionId, LessonWords words, Guid modelId, int templateId, CancellationToken cancellationToken);

    Task<bool> EndAsync(Guid positionId, LessonOutcome why, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **A lesson is due once its position has its result.** | `PositionResults.RecordAsync` of the Decision Engine writes the row of the lesson with the result, in the same transaction, due at once: see "The results of a position" in [orders.md](orders.md). So a position has its lesson to be written exactly when it has its result, which is some minutes after its close. |
| **A lesson that its user deleted is not written again.** | The row is gone, and its position has its result: nothing writes a row for it a second time. |
| **A position whose result was recorded before the table was added has no lesson.** | The migration `AddLessons` creates the table, and fills nothing. So no model is called for the trades of the past when the application is deployed. |
| **The model is the one that the Risk Manager of the agent has now,** and the key is the one of the agent's user now. | `IAgentTeams` of Agents says both. The model that held the role when the position was opened, which `position_roles` names, may no longer be the one that the user gave it. The row keeps the model that wrote the lesson. |
| **The model is sent the closed position, and what the team answered when it was opened.** | What it was traded on, its side, the prices that it was opened with, how it was closed, how long it was held, and its result, with the plan of the Strategist and the review of the Risk Manager as the record of that cycle keeps them. See "The lesson of a closed position" in [prompts.md](prompts.md). |
| **It answers the schema of a lesson,** and its answer is validated. | `RoleSchemas.Lesson`: `setup`, `expected`, `happened`, `takeaway`, and `tags`. `IRoleResults.ReadLesson` holds the answer to the schema and to the limits of a lesson, with the rules that hold the answer of a role: see "Validating a result" in [team.md](team.md). A takeaway of 61 words is rejected as `takeaway: TextTooLong`. |
| **A lesson that holds what a prompt never holds is rejected** (FR-PRM-9). | A secret, an email address, or an identifier: `answer: HoldsWhatAPromptNeverHolds`. A later prompt reads the lesson, and would be refused for it. |
| **A call that timed out, or that gave an invalid lesson, is made once more,** and no more. | As a role is called once more in a cycle (FR-TEAM-9), with the same prompt, and with the key asked for again for that call alone (FR-KEY-9). A call that failed, or whose model the provider does not have, is not made again. |
| **Every call is billed to the user of the agent, so there are two at the most.** | A position whose model gave no valid lesson has none, and nothing calls for it again, in the next run or later. Its row says why. |
| **A call that the gateway holds back is no call of the two.** | The calls to the provider, or the calls with the key, are paused after failures in a row (NFR-REL-4): nothing was sent, and nothing billed. The lesson is left as it was taken, and is due again an hour later. See "The breakers of the AI providers" in [reliability.md](reliability.md). |
| **A key that the provider refuses is checked again,** once. | Which marks it, and tells what depends on it (FR-KEY-3, FR-KEY-10). The call is not made again with it. |
| **A lesson is taken before its model is called.** | `ClaimAsync` moves when it is due an hour on, only while it is due and has no outcome. A run that is stopped while the model is called leaves the lesson for a later run, and the model is not called for it again at once. |
| **A row is completed once.** | An update changes a row only while it has no outcome. So a row that went with its agent while the model was called fails nothing, and a row that has its outcome keeps it. |
| **One lesson at a time, the longest due first, and at most 50 with a run.** | `LessonWriting.Most`. The calls of the lessons are outside the queue of a close, and one at a time, so they add one call at the most to what the cycles ask of a provider. |
| **A run starts no lesson after 2 minutes.** | `LessonWriting.StartsWithin`. A call may take as long as its provider is given, and may be made once more: so a run is over before the next one is due. What is left waits for that run. |
| **It works across users.** | A job has no user who is signed in. Each row says whose it is: everything that is read for a lesson is read for that user, the key is that user's, and what is written is written to that row alone. See "What is not asked for by owner" in [ownership.md](ownership.md). |
| **It places no order, and reads nothing of the exchange.** | What it needs of a position is stored. A lesson is text, which a later prompt names as data: nothing of it is carried out (NFR-SEC-6). `TeamRulesTests` holds the types to it. |
| **No test application runs it on a schedule.** | A test runs it itself, from the key `EcnanibApplicationFactory.LessonWritingKey`: see [background-work.md](background-work.md). |

## Recalling lessons

`ILessons` answers what a prompt recalls. It is handed the user by id, as `ITrackRecords` is.

```csharp
public interface ILessons
{
    Task<IReadOnlyList<string>> RecallAsync(Guid userId, LessonScope scope, CancellationToken cancellationToken);

    // And what the pages ask: see "The pages of the lessons".
}

public sealed record LessonScope(Guid AgentId, string Symbol, Market Market, Timeframe Timeframe);
```

`LessonRecall.Of` is the rule of which lessons are recalled. It is a rule only: it reads nothing.

| Rule | Detail |
|---|---|
| **The lessons of the agent first, the most recently closed first** (FR-EXP-7). | Whatever the agent traded when each was written, and in whatever mode: what an agent learned stays its own. |
| **Then those of the user's other agents on the same symbol, market, and timeframe.** | The ones that the agent trades now, which the scope says. The most recently closed first. |
| **At most `Experience:LessonsPerPrompt` of them.** | 5 in the tracked file. With 0, a prompt recalls none. |
| **Within a fixed size budget:** 4,000 characters of the lessons of one prompt. | `LessonRecall.BudgetCharacters`. It is no setting. The longest lesson that there can be is about half of it, so a prompt of an agent that has a lesson recalls one, and five lessons of the size that the instructions ask for fit. |
| **A lesson is recalled whole, or not at all.** | Cut short, it would say something else. The first that does not fit ends the recall, so an older lesson never stands in for a more recent one. |
| **Only a lesson that is written and enabled.** | A lesson that is disabled, one that was deleted, one that waits, and one that could not be written are in no prompt. |
| **Only the lessons of the same user.** | The question starts with `OwnedBy(userId)`: a lesson of another user is not recalled, whatever its agent trades (NFR-DAT-9). `LessonsTests` has the test with two users, which is marked for `lessons`. |
| **They are recalled once in a cycle, before the Strategist is called,** and the same go to both roles. | `RecalledLessons` of the Decision Engine asks, for the user of the cycle's agent, and `TeamCycle` hands what it answers to the prompts of the Strategist and of the Risk Manager. The two analysts are handed none. See "What a role is given" in [team.md](team.md). |
| **A test cycle recalls them as any cycle does.** | It composes the same prompts (FR-CYC-12). |
| **A stored lesson that holds what a prompt never holds is left out,** and the others go on. | A lesson is screened when it is written, so none should. What the screen finds can change after a lesson was stored, with a secret of the configuration file, and a prompt that held such a lesson would be refused. So a cycle is never given up for a stored lesson. The log says how many were left out. |
| **Lessons that cannot be read are not waited for.** | The team decides with none, and the log says so, as an error. |

## The screens

The pages are rendered on the server for each request, and none is interactive. Each is for a signed-in user: a visitor is sent to sign in. The bar at the top of every page leads to the experience screen.

| Page | What it is |
|---|---|
| `/experience` | The experience screen (FR-UI-2): the track record of each provider, and of each model, from the closed trades of the user, and under them the strength score of each model in each role, market, and timeframe. Each model leads to its page, with the same filter. |
| `/experience/models/<id>` | The experience record of one model, which the catalog links each model to (FR-CAT-10): its track record in any role, and in each role, and under them its strength score in each role, market, and timeframe. A role that the model never held says that no closed trade is counted, and no measure. |
| `/experience/lessons` | The lessons of the user, which they read, disable, and delete (FR-EXP-8): see "The pages of the lessons". The experience screen leads to it. |
| `/experience/board` | The platform-wide board (FR-EXP-10): the track record of each provider, and of each model, from the closed trades of all users, where enough of them contribute. A user leaves the board there, and comes back to it: see "The page of the board". The experience screen leads to it, with the same filter. |

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
| **The records are asked for by owner.** | A page hands `ITrackRecords` the user who is signed in. The catalog is the same for everyone, and the page of a model has one address for every user: each sees the record of their own trades there. The board alone is of every user, and names none: see "The page of the board". |
| **What a provider wrote is shown as text.** | The name of a model, like everything else on a page (NFR-SEC-4). |
| The times are in the time zone of the page (NFR-USA-3). | As on the other pages: see [times.md](times.md). |

The texts of the screens are in `ExperiencePages`, in one place, with how a filter is named in an address and read from one.

### The strength scores

Each screen has the section "Strength by role" under its track records, which `RoleStrengthsTable` shows. `StrengthPageTests` reads it as a signed-in user does.

| Rule | Detail |
|---|---|
| **The formula is shown, whatever there is to show** (FR-EXP-4). | "Strength score = measure × n ÷ (n + 20), where n is the sample size", with the damping of the configuration file, and from how many samples on a slice has a score. So is how each role is measured, and that a vetoed plan is followed as a shadow trade, which never places an order. |
| **A slice is a row:** model, provider, role, market, timeframe, samples, measure, and strength score. | The page of a model shows the slices of that model, and a row does not name it again. |
| **A slice below the minimum sample says "Not enough data",** in place of its measure and its score. | It says its sample. |
| **A measure that cannot be said is "Not available",** and so is its score. | A Risk Manager with results on one side only. |
| **A measure and a score say their unit and their sign.** | `+25 percentage points` for an analyst, which is right 75 times in 100, and `+1.5 R` for the Strategist and the Risk Manager. So a gain is told from a loss without a colour (FR-THM-5). |
| **The sample of a Risk Manager says what it is made of.** | `6 (4 approved, 2 vetoed)`. |
| **The scores are of the mode that the page shows,** and by its filter. | The section has no badge of its own: a page has one. The table says its mode in its name. |
| **The mode is chosen by the closed trades,** as before. | With no mode chosen, a page shows Live where it has enough closed trades. The calls of the analysts are many more than the trades, and do not choose it: the link to the other mode shows the scores of that mode. |
| **What the scores leave out is said,** each as a note. | The calls that are not scored yet, the calls that could not be scored, the vetoed plans that are followed still, and the ones whose shadow trade has no result, with why. |
| **A screen with nothing measured says that,** and has no table. | The formula is there all the same. |
| **The rows are in the order of the providers and the models,** and never of a score. | The screen ranks nothing. |

### The pages of the lessons

A user reads all of their lessons, disables or deletes any lesson, and resets an agent's lessons (FR-EXP-8). `ILessons` answers the pages, each question for the user who is signed in: `OfUserAsync`, `FindAsync`, `SetEnabledAsync`, `DeleteAsync`, and `ResetAsync`. `LessonPageTests` reads the pages as a signed-in user does.

| Page | What it is |
|---|---|
| `/experience/lessons` | Every lesson of the user that is written, the most recently closed first, 25 to a page. With `agent=<id>`, the lessons of one agent of theirs. A form of the page disables a lesson, or enables it again. The experience screen leads to it, and the pages of an agent to the lessons of that agent. |
| `/agents/<id>/lessons/<lesson>/delete` | Asks once more, and deletes one lesson. A page of the agent: see "The pages" in [agents.md](agents.md). |
| `/agents/<id>/lessons/reset` | Asks once more, and deletes every lesson of the agent. A page of the agent as well. |

| Rule | Detail |
|---|---|
| **A lesson is shown with what was traded and how the trade ended.** | Its heading is the symbol, the market, the timeframe, and the result in R, with its sign, or "no result in R" where its position has none. Under it are the agent, by the name that its user gave it, the mode of the trade, when the position was closed, the four parts, and the tags. |
| **A lesson says its mode as a word.** | The page shows lessons of both modes, and has no badge (FR-BIN-11). A page of an agent has the badge of the agent, and says the mode of a lesson as a word there too: a lesson that was written in Demo is shown for an agent that is in Live. |
| **The page says what a lesson is, who writes it, and under whose key** (NFR-SEC-8, NFR-USA-2). | The model of the agent's Risk Manager, in a call under the user's own key, which is sent the closed position, the plan that opened it, and the review of that plan. And where lessons are recalled, with the count of the configuration file. |
| **It says what it leaves out.** | How many closed positions wait for their lesson, and how many have none because none could be written, each as a note. |
| **Disabling and enabling take one press of a button.** | Each lesson of the page has a form of its own, named after it. The page answers with itself, by its own address, so a reload sends no form again. One press undoes the other, and neither places an order. |
| **Deleting a lesson and resetting the lessons of an agent each ask once more,** on a page of their own. | Neither can be undone. Neither takes a proof of who the user is: it exposes no key and moves no money. A form that is sent twice finds nothing the second time, and is answered as the first one was. |
| **A lesson that is deleted is gone for good,** and is not written again. | The page says so, and that disabling keeps the lesson and leaves it out of the prompts. |
| **Resetting deletes every lesson of the agent,** with those that wait to be written. | So that no lesson of a trade from before the reset is written after it. The lessons of the user's other agents stay, and the agent recalls those still. The trades stay: a lesson is not a trade. |
| **The choices are in the address:** `agent` and `page`. | A choice that is absent or empty is not made. An address that names a choice which there is not is not found: an agent that is none of the user's, a page that there is not, a choice that is made twice, and an id or a number that is written in another way than the page writes it. `LessonPages.ChoicesOf` reads the address. What it names besides is not gone by. |
| **A lesson of another user is not found,** like one that does not exist. | And no page has a form for one: a form that is posted all the same is the form of no page, or is answered as for a lesson that is gone already, and changes nothing. |
| **What a model wrote is shown as text.** | Like everything else on a page (NFR-SEC-4). |
| **It states what every experience screen states.** | A lesson is what one trade taught: the smallest sample that there is. |

The texts of the pages are in `LessonPages`, in one place.

### The page of the board

`/experience/board` shows what `IBoardRecords` answers, with the filter of the experience screen, in the address in the same way. `BoardPageTests` reads it as a signed-in user does.

| Rule | Detail |
|---|---|
| **It is for a signed-in user,** as every experience screen is. | A visitor is sent to sign in. The board is for the users of the service, whose trades it is made of. |
| **It shows the same tables as the experience screen:** by provider, and by model. | With the same columns, written in the same way. A model leads nowhere: the page of a model is of the reader's own trades. |
| **It says what it never shows, and when a record is shown.** | "The board shows no user, no balance, and no single trade", and that a provider or a model has a record only when at least 5 users and 50 closed trades contribute to it, with the numbers of the configuration file. |
| **A record that too few contribute to is not on the page at all.** | Not as a row that says "not enough", and not as a number of what is left out. A page with no record says that no provider, and no model, has one. |
| **It says no number of trades but in a record that is shown.** | The experience screen says how many trades the other mode would count, and how many wait for their result. The board says neither: such a number is of trades that too few may contribute to. |
| **The mode is under the heading, as a badge.** | With why it is that mode, that Demo and Live are never added up, and a link to the other mode. |
| **It states what every experience screen states,** and for whom a trade counts. | See "What every screen states". |
| **Under the records, the user reads whether their own trades are counted,** and leaves the board or comes back to it with one press of a button. | The page has one form at a time, named for what it does: `leave-board`, or `return-to-board`. It answers with itself, by its own address, so a reload sends no form again, and the filter stays. A form that is sent again is the form of no page. Neither takes a proof of who the user is: it exposes no key and moves no money. |
| **The choice is that of the user who is signed in.** | No form names a user. It is the one thing on the page that is of a user. |
| **A form that is sent does not have the board worked out.** | The answer leads to the page again, and the board reads the trades of every user. |
| **The account page says that there is a choice.** | Under "Your data", with a link to the board, and that the choice is stored with the account and is in a download. |

The texts of the page are in `BoardPages`, in one place.

### What every screen states

Every experience screen states, under the heading "What these figures cannot tell" (FR-EXP-11):

- that past results do not predict future results,
- that small samples are unreliable, with the sample size that a record is marked below,
- and that Demo results differ from Live results.

`ExperienceNotes` states all three, and asks for nothing of the page that shows it. So a page states them whatever it has to show, also when it has no trade to show. `ExperienceStatementTests` asks every route of `Ecnanib.WebUi.Pages.Experience`, the page of the lessons and the board among them, with no trade, with trades in Demo, and with trades in both modes, so a screen that is added fails it until it makes the statements.

## The settings

| Key | What it is | In the tracked file |
|---|---|---|
| `Experience:MinSamples` | The sample size below which a record is marked as a small sample, and from which Live data is enough to go by. It is also the sample size below which a slice shows "Not enough data", and has no strength score (FR-EXP-4). | 20 |
| `Experience:Damping` | The constant that damps a strength score toward zero for small samples (FR-EXP-4): the 20 of measure × n ÷ (n + 20). A whole number, 0 or more. | 20 |
| `Experience:ShadowFeePercent:Spot` | The standard fee of an order of a shadow trade on the Spot market (FR-EXP-5), in percent of what the order is worth, from 0 to 1. | 0.1 |
| `Experience:ShadowFeePercent:Futures` | The same on the Futures market. | 0.05 |
| `Experience:LessonsPerPrompt` | The most lessons that a prompt of the Strategist or of the Risk Manager recalls (FR-EXP-7). A whole number, 0 or more: with 0, a prompt recalls none, and lessons are written all the same. | 5 |
| `Experience:Board:MinUsers` | The fewest users that must contribute to a record of the platform-wide board for it to be shown (FR-EXP-10). A whole number, 1 or more. | 5 |
| `Experience:Board:MinTrades` | The fewest closed trades that must contribute to it. A whole number, 1 or more. | 50 |

The size budget of the recalled lessons and the limits of a lesson are fixed, and are no settings: see "Recalling lessons" and "Lessons". How a setting reaches a component is in [configuration.md](configuration.md).

## The data

A track record has no table. It is worked out from `positions`, `position_results`, and `position_roles`, which the Decision Engine keeps: see "The record of an order" in [orders.md](orders.md). So it cannot say something else than the positions say. A strength score has no table either, and neither has a record of the platform-wide board.

Experience has four tables. Two are for what later candles are to say something of, the third is for the lessons, and the fourth is for who left the board:

| Table | Columns |
|---|---|
| `analyst_calls` | `cycle_id`, `role`, `user_id`, `model_id`, `mode`, `market`, `symbol`, `timeframe`, `closed_at`, `direction`, `price`, `due_at`, `outcome`, `later_price`, `scored_at`. One row for each analyst of a cycle that called a direction: `(cycle_id, role)` is its key. Indexed by `(user_id, closed_at)`, and by `due_at` over the rows that have no outcome. |
| `shadow_trades` | `cycle_id`, `user_id`, `model_id`, `mode`, `market`, `symbol`, `timeframe`, `closed_at`, `side`, `entry_price`, `resting`, `stop_loss`, `take_profit`, `due_at`, `outcome`, `result_in_r`, `exited_at`, `evaluated_at`. One row for the vetoed plan of a cycle: `cycle_id` is its key. Indexed in the same two ways. |
| `lessons` | `position_id`, `user_id`, `agent_id`, `mode`, `market`, `symbol`, `timeframe`, `closed_at`, `result_in_r`, `due_at`, `outcome`, `setup`, `expected`, `happened`, `takeaway`, `tags`, `model_id`, `template_id`, `written_at`, `enabled`. One row for the lesson of a closed position: `position_id` is its key. Indexed by `(user_id, closed_at)`, by `(user_id, agent_id, closed_at)`, and by `due_at` over the rows that have no outcome. |
| `board_opt_outs` | `user_id`, `opted_out_at`. One row for a user who left the platform-wide board, and none for a user who contributes: `user_id` is its key. |

| Column | What it holds |
|---|---|
| `mode`, `market`, `timeframe` | The name of a member of `TradingMode`, `Market`, and `Timeframe`: those of the agent when the cycle ran. |
| `symbol` | A symbol of Binance that the configuration file lists. It is listed in `PersonalDataRules` as that. |
| `closed_at` | The close of the candle that the cycle was for. |
| `model_id` | The model of the catalog that made the call, or that vetoed the plan. A model is never deleted (FR-CAT-5). |
| `analyst_calls.role` | The name of a member of `Role`, which is the key of its row in `roles`: `MarketAnalyst` or `SentimentAnalyst`. |
| `analyst_calls.direction`, `price` | `Bullish` or `Bearish`, and the close of the cycle's candle. |
| `analyst_calls.outcome`, `later_price`, `scored_at` | Null until the call is scored. `Matched`, `Missed`, or `NoCandles`, with the close of the 4th later candle, and when it was scored. |
| `shadow_trades.side`, `entry_price`, `resting`, `stop_loss`, `take_profit` | `Long` or `Short`, the price that it is entered at, whether that is the price of an entry that rests, and the stop-loss and the take-profit of the plan. |
| `shadow_trades.outcome`, `result_in_r`, `exited_at`, `evaluated_at` | Null until the trade has ended. `StopLoss`, `TakeProfit`, `TimeUp`, `NotEntered`, `Rejected`, or `NoCandles`, with its result in R where it has one, the close of the candle that ended it, and when that was found. |
| `due_at` | When the row is next looked at. For a call it never changes. For a shadow trade it moves to the close of the next candle while the trade is open. For a lesson it moves an hour on when the lesson is taken for its model to be called. |
| `lessons.agent_id`, `mode`, `market`, `symbol`, `timeframe`, `closed_at`, `result_in_r` | What the position of the lesson says: its agent, what it was traded on, when it was closed, and its result in R, which is null where it has none. |
| `lessons.outcome`, `written_at` | Null while the lesson waits. `Written`, `NoModel`, `NoKey`, `NotComposed`, or `NoResult`, with when the row was given it. |
| `lessons.setup`, `expected`, `happened`, `takeaway` | Null until the lesson is written. What its model wrote, each on one line. They are listed in `PersonalDataRules` as that. |
| `lessons.tags` | The names of members of `LessonTag`, as `text[]`, in the order of the set. Null until the lesson is written. |
| `lessons.model_id`, `template_id` | The model of the catalog that wrote the lesson, and the version of the lesson instructions that it was written from (FR-PRM-6). Null until the lesson is written. Neither a model nor a version is ever deleted. |
| `lessons.enabled` | Whether the lesson may be recalled into a prompt. True until its user disables it. |
| `board_opt_outs.opted_out_at` | When the user left the board. Leaving again keeps it, and coming back deletes the row. |

- **A row is written once, with its cycle, and completed once, by the job.** Nothing else changes it. A lesson is written once with the result of its position, and completed once: after that its user alone changes it, by disabling it, enabling it, or deleting it. A row of `board_opt_outs` is written when its user leaves the board, and deleted when they come back.
- **All four tables hold rows of users.** `AnalystCall`, `ShadowTrade`, `Lesson`, and `BoardOptOut` implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. `RoleStrengthsTests` has the test with two users for the first two, `LessonsTests` the one for `lessons`, and `BoardRecordsTests` the one for `board_opt_outs`.
- **Who left the board points at the account itself:** `board_opt_outs (user_id)` at `users (id)`, with a key that deletes the row with the account. A user who has no agent can leave the board, so the row hangs on nothing else. **That key is declared by Agents,** in `BoardKeys.cs`: Experience may not use Accounts, Accounts uses no other component, and Agents may use both. `ExperienceModelTests` holds the name of the key.
- **Who left the board is in the export,** in the section `boardOptOuts`: one row with when the user left, or none. It is a preference of the user, the first that is stored, which Agents hands over, as it does the lessons. It says nothing of who the user is, an id and a time, so no column of it is listed in `PersonalDataRules`.
- **A row hangs on its cycle, and carries its owner along:** `(cycle_id, user_id)` points at `decision_cycles (id, user_id)`, with a key that deletes the row with the cycle. So the rows of an agent go with it, and those of an account with the account. A row names no agent in a column of its own.
- **That key is declared by the Decision Engine,** in `ExperienceKeys.cs`, because Experience may not use it. So each of the two entities has two configurations: its own, in Experience, and that of its key. See "The model" in [persistence.md](persistence.md). `ExperienceModelTests` holds the names of the keys.
- **A lesson hangs on its position, and carries its owner along:** `(position_id, user_id)` points at `positions (id, user_id)`, with a key that deletes the row with the position. So the lessons of an agent go with it, and those of an account with the account. And `template_id` points at `prompt_templates`, with a key that keeps a version that a lesson names.
- **Those two keys are declared by the Decision Engine as well,** in `LessonKeys.cs`, each in a configuration that is named to apply after the table that it points at. `ExperienceModelTests` holds the names of the keys.
- **A lesson names its agent in a column of its own,** unlike a call and a shadow trade: the lessons of the agent are recalled first, and are what a reset deletes, and Experience cannot ask the positions whose they are. The column has no key of its own: the row goes with its position, which goes with the agent.
- **Neither table of the calls and the shadow trades is in the export of a user's data.** Both are listed in `ExportRules.NotExported`, as the cycles that they hang on are: see "Exporting your data" in [accounts.md](accounts.md).
- **The lessons are in the export,** in the section `lessons`: every row, a lesson that waits or could not be written with which it is. Agents hands them over: Experience may not use Accounts, which declares what an export is made of, so that what recalls lessons into prompts stays out of reach of personal data (FR-PRM-9).
- **A cycle that was stored before the tables were added has no rows.** The migration `AddExperienceScoring` creates the tables, and fills nothing. A position whose result was recorded before `AddLessons` has no lesson.
- **A row of Experience goes with its cycle,** and the record of a cycle is kept for the days of `Retention:DecisionDays`, a year in the tracked file (NFR-DAT-5). So the calls and the shadow trades that a strength score counts are those of that time, for a score of all time as well. A lesson hangs on its position, and is kept indefinitely. See "The record of a cycle" in [retention.md](retention.md).

## The log

```
{Calls} calls of analysts were scored, and {ShadowTrades} shadow trades ended
The candles of {Symbol} on {Market} in {TradingMode} could not be read for Experience ({ExchangeCallOutcome}). What it was to score of it waits for the next run
The candles of {Symbol} on {Market} in {TradingMode} are not to be had: the configuration file does not list the symbol. What Experience was to score of it ends with no result
```

What has the lessons written says, under `Ecnanib.DecisionEngine.LessonWritingJob`, `LessonWriting`, `LessonRuns`, and `RecalledLessons`:

```
{Lessons} lessons of closed positions were written, and {Unwritten} positions were left without one
The lesson of the position of agent {AgentId} in {Symbol} was written
The lesson of the position of agent {AgentId} in {Symbol} was not written: {LessonOutcome}
The lesson of a position of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}
The lesson of a position of agent {AgentId} is not written: user {UserId} has no key for the AI provider {Provider} that can be used ({KeyHandOverOutcome})
The lesson of a position of agent {AgentId} is not written: its part "{PromptPart}" would hold {PromptLeak}
The {Role} of agent {AgentId} gave no valid lesson of a closed position
The lesson of the position {PositionId} could not be written
{Lessons} lessons of agent {AgentId} are recalled into no prompt: each holds what a prompt never holds
The lessons of agent {AgentId} could not be recalled, so its team decides with none
```

A lesson that is written is information, and one that is not is a warning, with why. A rejection says the field and the rule, in the service's own words. **No event holds anything that a model wrote:** not a part of a lesson, and not what a rejected answer said.

What stores who left the board says, under `Ecnanib.Experience.BoardContribution`:

```
User {UserId} left the platform-wide board
User {UserId} contributes to the platform-wide board again
```

Each is information, said once for a change: a choice that is made twice says nothing the second time. An event names the user by id, as the events of an account do. The board itself logs nothing: it reads, and its answer names no one.

A run that completed something says how much, as information, and a run that completed nothing says nothing. Candles that could not be read are a warning, said once for each symbol with each run, and so are candles that are not to be had. An event names a symbol, a market, and a mode, and no user: the candles of a symbol are the same for everyone.

## Testing

`TrackRecordTests` and `ExperienceBasisTests` hold the measures and the rule of Live or Demo to the tables above, without a database. `ExperienceTextTests` does so for how a record and a strength score are written and how a filter is named in an address.

`StrengthScoreTests` holds the formula, `RoleMeasuresTests` the measure of each role, `CallScoreTests` the score of a call, and `ShadowTradeRuleTests` the shadow trade, each without a database: a shadow trade is handed the candles that its test names. `ExperienceModelTests` reads the model and the architecture rules: the names of the keys to the cycle, and that nothing of Experience can reach the orders of the gateway.

`ClosedPositionTests` reads the trades as Experience does, through `IClosedTrades`, and has the test with two users, which is marked for `positions`, `position_results`, and `position_roles`. `TrackRecordsTests` asks `ITrackRecords`, and `ExperiencePageTests` reads the screens as a signed-in user does.

`BoardSliceTests` holds when a slice of the board is shown to the numbers of the specification, without a database: 4 users, 49 trades, one user with many trades, and a trade that is in no measure. `BoardRecordsTests` asks `IBoardRecords` and `IBoardContribution`: the same record as `TrackRecord.Of` gives, a record that too few contribute to, a model beside its provider, the modes, the filter, a user who leaves and comes back, an agent that is deleted, and the test with two users, which is marked for `board_opt_outs`. It reads the types of an answer as well, which hold no user and no trade. `ClosedPositionTests` reads the trades of the board as Experience does, through `IBoardTrades`. `BoardPageTests` reads the page as a signed-in user does, presses its buttons, and reads the section of the export. Those tests take 3 users and 6 trades as what must contribute, with a file of their own, and `BoardContributors.GiveContributorsAsync` gives a test several users with an agent and closed trades each.

`CycleExperienceTests` stores cycles as the application does, and reads what each left. `ExperienceScoringJobTests` runs the job itself, at moments of its choosing, with `StandInCandles` in place of the candles of the exchange, which answers each ask with the candles that the test names: no test application runs the job on a schedule. `RoleStrengthsTests` asks `IRoleStrengths`, and has the test with two users, which is marked for `analyst_calls` and `shadow_trades`. `StrengthPageTests` reads the section of the strength scores on both screens.

`LessonTests` holds the limits of a lesson, that each of its parts is one line, and the rule of what a prompt recalls, with its count and its budget, without a database. `LessonsTests` asks `ILessons` and `IDueLessons`: the order of the recall, across the modes, what is not recalled, and the test with two users, which is marked for `lessons`. `LessonPageTests` reads the pages of the lessons, presses their buttons, and reads the section of the export.

`LessonWritingJobTests` runs the job itself, with `StandInModels` in the place of the AI providers and `StandInKeys` in the place of what hands a stored key over: the lesson of a closed position, written once, the one call more, a model that gives none, a user with no key, a Risk Manager with no model, two users, a run that starts no lesson late, and a run that is stopped. `LessonPromptTests` holds the prompt of the call, the schema of a lesson, and its validation. `CycleLessonTests` runs cycles as the application does, with agents that are stored, and reads the lessons in the prompts that their models were sent.

`StoredLessonRows.GiveLessonAsync` gives an agent a closed position with its result and the lesson of that position, as it is once its model has written it, or as a test says: one that waits, or one that could not be written. A test tells one lesson from another by its takeaway. `StoredOrderRows.GiveOrdersAsync` leaves the lesson of its position to be written, as recording a result does: so a cycle that a test stores with its orders has one that waits, which no prompt recalls.

`StoredTradeRows.GiveTradeAsync` gives an agent a closed position with its roles and its result, stored as the application stores them, without the cycle and the orders that lead to one. A test says of it what it is about: its result in R, its mode, its market, its timeframe, whether its result is complete, and whether it is recorded at all. Most of these tests take 3 trades as the minimum sample, with a file of their own, so that a test has enough of them with a few.

`StoredExperienceRows.GiveCallsAsync` and `GiveShadowTradeAsync` give an agent calls and shadow trades as they are once they are scored, each on a cycle of its own that has no calls, without the candles that score them. `GiveCycleAsync` of the tests of the Decision Engine stores a whole cycle as the application does, which leaves the call of its Market Analyst, and the shadow trade of its plan when it ended with a veto: a test that needs a row in every table of users gives its agent one of each.

## Not built yet

- Recommending a team from anything but the slice of the agent's own market and timeframe. Agents recommends the strongest candidate of each role from the strength scores, and reassigns the roles of an agent that opted in (FR-TEAM-4, FR-TEAM-10): see "Role by strength" in [agents.md](agents.md). A model that was measured on another market or timeframe has no score there, and is recommended nothing from it.
- The funding of a shadow trade. A position of Futures receives and pays funding, and a shadow trade is charged its fees alone: the specification names standard fees, and the funding rates of the past are not kept.
- A shadow trade of a plan that was approved and then opened no position: one that the Risk Guard rejected, one whose entry was skipped, and one whose entry at a price was not reached. Only a plan that was executed has a result on the approved side.
- Following a shadow trade for longer than 200 candles, and scoring what the candles of the exchange no longer reach. The gateway reads the newest candles alone: see "Market data" in [exchange.md](exchange.md).
- Scoring the cycles that were recorded before the tables were added. They left no rows.
- Saying in the record of a cycle which lessons its prompts recalled. A lesson can be disabled or deleted afterwards, and the record of a cycle holds no prompt: see "The record of a cycle" in [team.md](team.md).
- A record of the calls that write lessons. A lesson says the model that wrote it and the version of its instructions. How long a call took and how many tokens it used are in the log of the gateway alone, and a call that gave no lesson leaves the outcome of its row. So these calls are in no figure of the AI usage, which is worked out from the recorded calls of the roles, and its page says so: see "AI usage" in [team.md](team.md).
- Lessons for the positions that were closed before lessons were written. Their rows were never left: see "Writing a lesson".
- Calling a model for a lesson again later, after it gave none. Every call is billed to the user, so there are two at the most.
- Keeping the lessons of an agent that is deleted. The specification keeps lessons indefinitely (NFR-DAT-5), and a lesson goes with its position, which goes with its agent, as a trade does.
- What the market did while a position was open, in the prompt of its lesson. Candles are not kept (NFR-DAT-6): the model is sent how the position ended, and not the path to it.
- A choice of lessons by what they are about. The tags are shown, and nothing recalls or filters by them.
- A stored aggregate of the board. The specification names an aggregated track record with a scope of user or platform, and nothing of either is stored: the board reads every counted trade of every contributor each time it is shown, and no index serves that read, since `positions (user_id, closed_at)` starts with the owner. That is as much work as there are closed trades, for every signed-in user who opens the page.
- Keeping two records of the board from saying together what neither may say alone. A slice is held to the least users and trades by itself, as the specification says. The difference of two that are shown, or of one at two moments, can be of fewer: see "The platform-wide board". A rule that hides a record for what another shows is not built, and neither is one against a single user who holds most of the trades of a slice.
- Keeping what is counted on the board from being made up. Demo trades cost nothing, so a few accounts can fill a slice of Demo, and anyone can register several.
- The strength scores on the board, and the page of one model across users. The board shows the track records, by provider and by model.
- Recommending a team from the board. See "Role by strength" in [agents.md](agents.md).
- The board for a visitor. It is for a signed-in user, as every experience screen is.
- Keeping the trades of an agent that is deleted. The specification keeps positions indefinitely (NFR-DAT-5), and deleting an agent deletes its positions: see "The record of an order" in [orders.md](orders.md). A track record follows the positions, so it loses those trades.
- A track record by symbol, by agent, or by exit reason. A trade says none of them to Experience.
