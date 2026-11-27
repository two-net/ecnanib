# Dashboard

The dashboard shows, per mode: total equity across agents, profit and loss for the day, open positions, a status card per agent, recent decisions, and alerts (FR-RPT-1). Demo results are labeled as Demo everywhere and are never combined with Live results (FR-BIN-11): the dashboard has a section for Live and one for Demo, and nothing of one is added to anything of the other.

It is one screen for all of the agents of the user who is signed in (FR-UI-2). Nothing of its figures is stored: each is worked out from the rows of that user each time the page is asked for.

A first-time user sees a setup checklist above them: add keys, create a Demo agent, review its decisions, then consider Live (FR-UI-4). Each step shows as done once its user has completed it. What the user says on it is the one thing that the page stores: see "The setup checklist".

The page is `DashboardOverview.razor` in `src/Ecnanib/WebUi/Pages/Dashboard`, with its texts in `DashboardPages.cs`. What it reads is `IStoredAgents` in `src/Ecnanib/Agents`, `IAgentEquities` and `IRecordedCycles` in `src/Ecnanib/DecisionEngine`, and `INotifications` in `src/Ecnanib/Notifications`. The setup checklist is `SetupChecklist.razor` beside the page, with `SetupProgress.cs` and its texts in `SetupPages.cs`, and reads `IStoredKeys` in `src/Ecnanib/Credentials` and `ISetupMarks` in `src/Ecnanib/Accounts` as well. What keeps the prices followed for it is `LiveFeed` in `src/Ecnanib/WebUi/Live`, with `IOpenPositions` of the Decision Engine and `IExchangePriceWatch` of the Exchange Gateway. The rules below are enforced by `DashboardPageTests` in `tests/Ecnanib.Tests/Dashboard`, by `AgentEquityTests`, `EquityTotalsTests`, `OpenPositionTests`, and `CycleRecordTests` in `tests/Ecnanib.Tests/DecisionEngine`, by `LiveFeedTests` and `LivePageTests` in `tests/Ecnanib.Tests/LiveUpdates`, by `NotificationTests` in `tests/Ecnanib.Tests/Notifications`, and for the setup checklist by `SetupChecklistPageTests` and `SetupProgressTests` in `tests/Ecnanib.Tests/Dashboard` and `SetupMarkTests` in `tests/Ecnanib.Tests/Accounts`.

What the equity of an agent is, is in "The equity of an agent" in [orders.md](orders.md), and what a loss limit counts from in "The loss limits" in [risk-guard.md](risk-guard.md). What a cycle is and how it ends is in [team.md](team.md), what a notification is in [notifications.md](notifications.md), how a page follows what changes in [live-updates.md](live-updates.md), and whose rows a request reaches in [ownership.md](ownership.md).

## Per mode

| Rule | Detail |
|---|---|
| **A section for each mode, and Live first.** | "Your agents in Live", and then "Your agents in Demo": real money is at stake in the first. Each says its mode with the badge under its heading, as a word, and what the mode is. A mode in which its user has no agent says so, in one line. |
| **Nothing of one mode is in the section of the other.** | Each part is asked for by mode, or handed over with its mode, and put under the mode that it says. No total crosses the two, and the page has no figure of both. |
| **The mode of a figure is the mode that its agent is in.** | The equity of an agent is the result of the mode that the agent is in: see "The equity of an agent" in [orders.md](orders.md). An agent that was switched to Live is worth its capital there, whatever it made in Demo, and is counted in Live alone. |
| **The mode of what happened is the mode that it happened in.** | A cycle says the mode that its agent was in then, and a notification the mode that whoever raised it said. So what a team decided in Demo, and what its user was told about Demo, stay under Demo when the agent is switched to Live: they are never read through the mode that the agent is in now. |
| **What is about no mode is above both.** | A notification about a key of an AI provider, which the agents of both modes use. See "Alerts". |

## Total equity, and profit and loss for the day

Agent equity is the capital the user allocated to an agent, plus the agent's net profit or loss to date. `IAgentEquities` reads it for every agent of a user, and `EquityTotals` adds it up:

```csharp
public interface IAgentEquities
{
    Task<IReadOnlyList<AgentEquity>> OfUserAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record AgentEquity(
    Guid AgentId, TradingMode Mode, string Asset, decimal Capital,
    decimal? Equity, decimal? MadeToday, OpenPosition? Position, WatchedPrice? Price);

public sealed record EquityTotal(
    TradingMode Mode, string Asset, int Agents, decimal Capital, decimal? Equity, decimal? MadeToday, DateTimeOffset? PricedAt);

public static class EquityTotals
{
    public static IReadOnlyList<EquityTotal> Of(IEnumerable<AgentEquity> equities);
}
```

| Rule | Detail |
|---|---|
| **It is the equity that the loss limits are held to, by one rule.** | `AgentResults.RowsAsync` works out what is settled of the result of an agent from its positions, its orders, its executions, and its funding, with the sums of `ResultSums`: the same rows, read the same way, as for a cycle and for a halt. Fees and funding are in it (FR-BIN-10). What differs is where the price of an open position comes from. |
| **Every agent of the mode counts.** | A draft, and an agent that its user paused or stopped, as one that runs: the equity of an agent that has traded nothing is its capital. |
| **The agents of a user are read at one moment.** | In one snapshot: a stop-loss that is executed while the page is written is in every figure of the page, or in none. See "Reads that agree" in [persistence.md](persistence.md). |
| **An open position is valued at the price that the watch has.** | `IExchangePriceWatch.Latest`: the mark price on Futures, and the price that the symbol last traded at on Spot, as `PositionValues.PriceOf` says for the equity everywhere. The watch has the price of what someone follows. **The page never waits for Binance,** and asks it nothing: see "Following a price" in [exchange.md](exchange.md). |
| **What is not known is said as not known, and never as a number.** | Where the watch has no price for an open position, the equity of its agent is not known, and neither is its day: "Not known at the moment". It is not what is settled of it, and not nothing. So is the result of an agent of which an order that closed a part of a position says no price yet. `AgentEquity.Equity` and `MadeToday` are empty then. |
| **A total that has a part which is not known is not known.** | It is not the sum of the agents that are: that would be a number which is wrong, and looks right. The page says why: which symbol has no price at the moment. The agents that are known say their own equity on their cards. |
| **A total is of one mode and one asset.** | The equity of an agent is in the asset that its symbol is priced in. Amounts in two assets are not added up: a mode whose agents are in two assets has a total for each, by the name of the asset. A user whose symbols are all priced in one asset has one. |
| **Profit and loss for the day is the equity now, less the equity as the UTC day began.** | The result as the day began is the one that `EquityMarks.At` says, which the daily loss limit of the agent is counted from: the first result that was worked out in the UTC day, at a cycle or a reconciliation. An agent that was not evaluated in the day has made nothing in it. It includes unrealized profit and loss, as the limit does (FR-RSK-8). |
| **A capital that its user changed today is no profit.** | The marks of an agent are worked out anew for its capital as it is now, so that the agent has lost the same share of its equity in the day as before: see "The loss limits" in [risk-guard.md](risk-guard.md). The amount of the day is then of that share, and not the amount that was lost before the change. |
| **It only reads.** | It writes no mark and no equity over time: what watches the loss limits does, when it evaluates them. Asking for the dashboard halts no agent. |
| **It logs no warning for a price that nobody follows.** | That is how a page finds a price that was not asked for yet, and no failure. |
| **The figures say when their prices were read.** | "Open positions are valued at prices as of 24 November 2026, 09:00:07 UTC": the oldest of the prices that are in the total, to the second. A price that could not be read for a while shows as the old one that it is. A total that no price is in says so. |
| **An amount is written to 2 decimal places,** | as a notification writes an equity, with the asset after it, and with no zeros after the last digit that counts: `1012.4 USDT`. A profit or a loss has its sign: `+12.4 USDT`, `-5.99 USDT`, `0 USDT`. So a loss is told from a profit without a colour (FR-THM-5), and nothing on the page has a colour of its own for either. |

## Open positions

The open positions of a mode are a table: for each, its agent, which leads to the page of the state of the agent, its symbol, its market, its side, what it holds, the price that it was entered at, its stop-loss and its take-profit, the price that it is valued at with when that was read, what it would make at that price, and since when it is open.

| Rule | Detail |
|---|---|
| **It is the position that the equity counts.** | `AgentEquity.Position` is the open position of the agent as the rows of its equity hold it, read in the same snapshot. An agent has one at the most. |
| **The protection is said as it rests on the exchange.** | The price of the stop-loss, and that of the take-profit, each when an order for it rests, and "None rests" otherwise: the price alone protects nothing. It is the rule of the page of the agent, in one place: `OpenPositions.Of`, with `RestingOrders`. See "The position on a page" in [orders.md](orders.md). |
| **What it would make is before fees and funding,** | and says so under the table. It is what `OpenPosition.UnrealizedAt` says at the price that the watch has, and "Not known at the moment" where it has none. |
| **An entry that is on its way is no position.** | A card says that an agent holds something by the position, and not by `AgentView.HasOpenPosition`, which is also so while an entry has not filled. |

## A status card per agent

Each agent of a mode has a card: its name, which leads to the page of its state, which is the screen of the agent (see [agent-screen.md](agent-screen.md)), its symbol, its market, and its timeframe, its state with since when, its equity, what it made today, and what it holds.

| Rule | Detail |
|---|---|
| **The state is said in the words of the pages of the agent.** | `AgentPages.Describe`, with the time of the latest change of it (FR-AGT-10). |
| **An agent that is halted, or that waits for its user, says why.** | `AgentPages.Says` of the cause, as the page of its state says it, and for a daily loss limit when the halt ends (FR-RSK-8). An agent in another state says its state alone: what its user has to know of at a glance is what stopped. |
| **A card says what is known of its agent.** | An equity that is not known says so on the card of that agent, and the cards of the others say theirs. |
| **The agents are in the order of the list of the agents:** | the oldest first. |

## Recent decisions

The recent decisions of a mode are a table of the latest cycles that a candle close started in that mode, the latest first, 10 at most: the close of the candle, which leads to the timeline of the cycle, the agent, and how the cycle ended. `IRecordedCycles` reads them:

```csharp
Task<IReadOnlyList<RecentCycle>> LatestOfUserAsync(Guid userId, TradingMode mode, CancellationToken cancellationToken);

public sealed record RecentCycle(Guid AgentId, CycleListing Cycle);
```

| Rule | Detail |
|---|---|
| **A decision is how a cycle ended.** | Its outcome, in the words of the list of the cycles of an agent: `CyclePages.Describe`. What each role answered is on the timeline. See "The outcome of a cycle" in [team.md](team.md). |
| **A cycle is of the mode that its row says.** | The mode that its agent was in then (FR-BIN-11). |
| **A test cycle is not among them.** | Its user ran it to see what the team would decide, and it placed no orders (FR-CYC-12). It is on the list of the cycles of its agent. |
| **It is asked for by owner, across the agents of the user.** | With an index of the cycles of a user in a mode by their closes, which `AddDashboard` adds: no other index of the cycles starts with their owner. |

## Alerts

The alerts of a mode are what the service told its user about that mode and they have not read, the latest first, 10 at most, with how many there are in all: what each is about, when it was raised, and what it says, as text. `INotifications.UnreadByModeAsync` reads them. See "The interface" in [notifications.md](notifications.md).

| Rule | Detail |
|---|---|
| **An alert is a notification that is not read.** | Of every kind, as on the page of the state of an agent: a position that was opened or closed, an agent that was halted or waits for its user, a key that is invalid, a model that is unavailable, an order or a protection that failed, records that did not match Binance, and a position that approaches liquidation (FR-NTF-1). The specification says "alerts" of what the service tells its user (FR-RSK-7, FR-BIN-F5), and names no other list. |
| **It is of the mode that the notification says.** | The mode is stored with the notification when it is raised, and stays as it is. One about a Binance key for Live is an alert of Live, though it names no agent. |
| **What is about no mode is listed above both sections,** | under "Alerts about your account", when there is any: a key of an AI provider, and a notification from before notifications said their mode whose agent is deleted. |
| **A form marks one as read.** | `mark-read-<id>`, as on the other pages. It leads back to the dashboard, by a request that changes nothing. A notification is on the page once, so its form is. A form that is no longer on the page, because newer alerts took its place, is answered with "bad request", and marks nothing. |
| **One that is about an agent leads to the page of the state of that agent,** | while its user has the agent. |
| **The link "All your notifications" leads to all of them,** | read and not read. |

## The setup checklist

A first-time user sees a setup checklist: add keys, create a Demo agent, review its decisions, then consider Live (FR-UI-4). It is a section of the dashboard, "Getting started", under what the page says of itself and above the alerts and the sections of the modes. It says how many of its four steps are done, and each step, in the order of the requirement, says whether it is done and what there is to do for it. `SetupProgress` works out where a user stands from what the page reads:

```csharp
public sealed record SetupProgress(bool AiKeyStored, bool DemoKeyStored, bool AgentCreated, AgentView? Decided, SetupMarks Marks)
{
    public static SetupProgress Of(
        StoredKeysState keys, IReadOnlyList<AgentView> agents,
        IReadOnlyList<RecentCycle> demoDecisions, IReadOnlyList<RecentCycle> liveDecisions, SetupMarks marks);

    public bool IsDone(SetupStep step);
    public bool CanMarkReviewed { get; }
    public bool CanMarkConsidered { get; }
    public bool Finished { get; }
}
```

| Step | Done when | While it is not done |
|---|---|---|
| Add keys | Its user has a key of at least one AI provider and a Binance key for Demo. | It says which of the two is missing, and leads to the API keys. |
| Create a Demo agent | Its user has an agent. Every agent is created in Demo (FR-AGT-7). | It leads to the form of a new agent. |
| Review its decisions | Its user said so, with the button "Mark as reviewed". | While no team of theirs has decided, it says so. Once one has, it names the agent, leads to the screen of that agent, where its decisions are, and has the button. |
| Consider Live | Its user said so, with the button "I have considered Live", or switched an agent to Live. | Before the review, it says that it comes after it. After the review, it says what Live is and what a switch takes (FR-AGT-7), leads to the agents and to the risk disclosure, and has the button. |

| Rule | Detail |
|---|---|
| **The first two steps say the account as it is now.** | They are read from the keys and the agents each time the page is written, `IStoredKeys.StateAsync` and `IStoredAgents.ListAsync`, and nothing is stored for them. A key that is deleted is a step that is open again, while the checklist is shown. A key counts while it is stored, whatever its last check found: a key that its provider refuses is told to its user by a notification. |
| **The Binance key is the one for Demo.** | It is what a Demo agent trades with, and a key for Live alone does not do the step. A key of one AI provider does: a user can give all four roles models of one provider (FR-AGT-5). |
| **The last two steps are what the user says.** | Nothing that is stored tells that someone read a decision, or weighed going to Live, and opening a page proves neither. So each has a form with a button, as a notification has one that marks it as read, and no page marks anything by being opened. |
| **A mark is stored with the account, with when it was set.** | `users.decisions_reviewed_at` and `users.live_considered_at`, behind `ISetupMarks` of Accounts: see "The setup marks" in [accounts.md](accounts.md). The first time stands, and no mark is taken back. Both are in the export of the account. |
| **There is a decision to review once a team of the user has decided.** | A cycle that the close of a candle started, among the latest that the page lists: one of Demo first, as the checklist leads there, and one of Live for a user who has none in Demo, so that whoever went straight to Live can finish the checklist. A cycle that its user ran as a test is no decision (FR-CYC-12). A decision alone does not do the step. |
| **Live is offered after the review.** | "Then consider Live": its button is on the page once the decisions are marked as reviewed. Staying in Demo is a choice too, and the step says so: the button says that Live was considered, and not that it was chosen. |
| **A switch of an agent to Live does the last step.** | Whoever switched an agent to Live has considered it. `AgentModes` stores the mark in the transaction of the switch, with `SetupMarking.RecordAsync`, beside the event of the audit trail: see "The switch to Live" in [agents.md](agents.md). An account that switched an agent to Live before there was a checklist is given the time of its first switch by the migration `AddSetupMarks`, from the audit trail. |
| **The checklist is shown until both marks are set, and then no more.** | Whoever reviewed decisions and considered Live is no first-time user. In the order of the checklist, all four steps are done by then. It does not come back when its user deletes a key or an agent later. For a user who has set both, the page reads the two marks, and nothing else for the checklist. |
| **The steps do not wait for one another,** | except Live for the review. A user who has no key yet creates an agent all the same: see [agents.md](agents.md). |
| **A form is on the page only while its step can be marked.** | `setup-reviewed` and `setup-considered`. One that is posted while it is not on the page is answered with "bad request", and marks nothing, as is one without the token of the page. Each leads back to the dashboard, by a request that changes nothing. They are the forms of `SetupChecklist.razor`, a part of the page, which answers them itself. |
| **Whether a step is done is said as a word** (FR-THM-5). | "Done" or "Not done yet", ahead of what the step says. A step has no colour and no mark of its own for it. The word, and how many steps are done, are told to the script of the pages as what they say now, so each fades in when it changes (FR-ANI-3): see [motion.md](motion.md). |
| **It changes as the page does.** | The page asks for itself again when an agent or a cycle of its user changes, so an agent that is created and a team that decides show with no reload. A key says nothing to a page that follows: the step of the keys is as of when the page was written, which is when its user comes back from the keys. See [live-updates.md](live-updates.md). |
| **It is on the dashboard alone.** | The start page, which a user arrives at after signing in, leads to the dashboard and reads nothing of the account: see "The start page" in [layout.md](layout.md). |

## The page

A signed-in user opens the dashboard at `/dashboard`, which the bar at the top of every page leads to. A visitor is sent to sign in.

| Rule | Detail |
|---|---|
| **It is rendered on the server for each request.** | As every page is. Its forms are posted with the token of the page: see [security.md](security.md). |
| **Everything is asked for by owner.** | Agents and Notifications take the request, and the Decision Engine, which may not use Accounts, is handed the user who is signed in. Nothing of another user is on the page: see [ownership.md](ownership.md). |
| **It follows what changes** (FR-NTF-2). | When an agent of its user, what one holds, a cycle, or a notification changes, the page asks for itself again: the subjects `Agent`, `Holdings`, `Cycle`, and `Notification`. So the states, the positions, the decisions, the alerts, and the count in the bar are those of now, without a manual refresh. |
| **It has the prices of what its user holds followed.** | The feed of the page follows the price of each symbol that an agent of its user holds a position in, while the page is open, so that the watch has them. The first time that such a price is read, the page asks for itself again, and is written with it. So a dashboard that was written with "Not known at the moment" shows its figures within a second or two. See "The feed of a page" in [live-updates.md](live-updates.md). |
| **A figure stays as it was written between two changes.** | A price that is read again asks for nothing. The figures are of the moment that the page was written, say the time of their prices, and are worked out anew when something that the page shows changes. |
| **A figure that changes counts to its new value** (FR-ANI-2). | The total equity and the day of each mode, the price and what each open position would make now, and the equity and the day of each agent: each number is set apart in its text, and the script of the pages counts it from its old value to its new one when the page is written again. The state of an agent fades in when it is another, and so does an agent, a position, a decision, or an alert that entered its list (FR-ANI-3). Nothing of it moves for a device that asks for reduced motion, nor for an account that reduced it. See [motion.md](motion.md). |
| **It leads to the downloads of the trades and the decisions** (FR-RPT-4). | Two links at its foot, to the CSV files of every agent of the user, in which each row says its mode. They are no part of a section of a mode: see [export.md](export.md). |
| **It can be paused** (NFR-USA-1). | Under its heading the page says that it follows what changes, with "Pause live updates". Paused, it makes no connection and is as it was written, until "Resume live updates" is followed. The forms of its alerts and of the setup checklist lead back to it paused. A paused page reads no price, and says so: see "Pausing" in [live-updates.md](live-updates.md). |
| **It has no field to type in.** | A page that follows has none: see "The one push channel" in [live-updates.md](live-updates.md). It has buttons and links: the forms of the alerts and the two of the setup checklist each have a button alone. |
| **Its texts are in one place.** | `DashboardPages` (NFR-USA-4), with how an amount is written, and `SetupPages` for the setup checklist: see [texts.md](texts.md). The times are in the time zone of the page, and each says how far that is from UTC (NFR-USA-3): see [times.md](times.md). |
| **The tables scroll by themselves on a narrow screen,** | also by keyboard, and the cards stand side by side as far as they fit (FR-UI-1). The look is in `app.css`: a page carries no style of its own. |

## Testing

| Test class | What it holds |
|---|---|
| `DashboardPageTests` | The page: each part of each mode, that Demo and Live are never added up, also for an agent that was switched, that what is not known is said so, that two assets are not added up, that nothing of another user is on it, the form that marks an alert as read, the link in the bar, and a visitor. |
| `AgentEquityTests` | The equity of each agent of a user: the capital alone, what was lost and the day that it was lost in, a position with a price and without one, the mark price of Futures, the mode of the agent, by owner, and that nothing is written. |
| `EquityTotalsTests` | What equities add up to, with no database: each mode and each asset on its own, a total that is not known, and the time of the oldest price. |
| `OpenPositionTests`, `CycleRecordTests` | The symbols that the agents of a user hold, and the latest cycles of a user in a mode. |
| `LiveFeedTests`, `LivePageTests` | That the prices of what a user holds are followed for the page, and when the page asks for itself again. That the dashboard carries one part for its connection, and no field. |
| `NotificationTests` | That a notification is stored with its mode, and what the migration made of those from before. |
| `SetupChecklistPageTests` | The setup checklist on the page: the four steps in their order with none done, each step as it is done, the two forms and what they store, that the checklist is gone for good once both are marked, a form that is not on the page, that nothing of another user counts, and a switch to Live. |
| `SetupProgressTests` | Where a user stands, with no database: what does each step, which decisions there are to review, when each button is offered, when the checklist is finished, and what each step says. |
| `SetupMarkTests` | The marks of Accounts: by owner, that the first time stands, a request that is not signed in, a mark in a transaction, and what the migration made of an account that switched an agent to Live before. |

No test of these drives a browser. `AccessibilityBrowserTests` does, when a run is told to, and opens the dashboard of a first-time user and of a user with agents, paused and following: see [accessibility.md](accessibility.md). By hand:

1. `docker compose up -d`, and run the application.
2. Sign in, and open `/dashboard`: "Getting started", with none of the 4 steps done. Create two agents, and open it again: the second step is done, and under the checklist are Live and then Demo, each with its badge, and an equity that is the capital.
3. Change a row at the database, such as `UPDATE agents SET state = 'Paused' WHERE id = '...'`: the card says the state within two seconds, with no reload.
4. Set both marks, `UPDATE users SET decisions_reviewed_at = now(), live_considered_at = now() WHERE email = '...'`, and load the page again: the checklist is gone.

## Not built yet

- Figures that move with the price between two changes: the total equity, the day, and what a position would make are of the moment that the page was written. Writing them over the connection, as the page of an agent writes its one position, needs the parts of the dashboard that hold a figure to be parts of that kind.
- What changes a figure and is said by no table that a page follows: a fee that is valued later, funding that is stored, and the UTC day that begins. The page shows it the next time that it is written.
- The dashboard as the page that a user lands on after signing in. A user arrives at the start page, `/`, which leads to the dashboard: see "The start page" in [layout.md](layout.md).
- The badge of the mode on the page of the notifications: see [notifications.md](notifications.md).
- A banner of its own for an outage of Binance. The users whose agents run where Binance is out are told with a notification, which the dashboard shows among its alerts as it shows every other (NFR-REL-4): see "A Binance outage" in [reliability.md](reliability.md).
