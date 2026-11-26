# Live updates

Agent status, positions, prices, and cycle progress update in the UI within 2 seconds, without a manual refresh (FR-NTF-2). Live UI updates use one push channel from the application to the browser (NFR-ARC-6). A user receives updates only for their own agents.

What is built is that, for the pages that show those things: the dashboard, the list of the agents, the pages of the state, the cycles, and a cycle of an agent, the page that runs a test cycle, and the page of the notifications. A page that follows is still rendered on the server for each request, with its forms as before. It carries one part for which it makes a connection, and over that connection the application says two things: that something which the page shows has changed, upon which the page asks for itself again, and the values that tick, which are written to the page as they change.

The signals are in `src/Ecnanib/Platform/LiveUpdates`, with the subjects in `src/Ecnanib/Shared/LiveSubject.cs` and the triggers of the database in the migrations `AddLiveSignals` and `AddEquitySignal`. What a page does with them is in `src/Ecnanib/WebUi/Live`. What it asks of the components is `IConnectedSession` in `src/Ecnanib/Accounts`, `ICycleProgress` and `IOpenPositions` in `src/Ecnanib/DecisionEngine`, and `IExchangePriceWatch` in `src/Ecnanib/ExchangeGateway`. The rules below are enforced by the tests in `tests/Ecnanib.Tests/LiveUpdates`, by `ConnectedSessionTests` in `tests/Ecnanib.Tests/Accounts`, by `CycleProgressTests` and `OpenPositionTests` in `tests/Ecnanib.Tests/DecisionEngine`, by `PriceWatchTests` and `RequestLimitTests` in `tests/Ecnanib.Tests/ExchangeGateway`, and by the reserved namespaces of [`ComponentRules.cs`](../tests/Ecnanib.Tests/Architecture/ComponentRules.cs).

## How a change reaches a page

```
a write is committed
  -> a trigger of its table notifies: the subject, the owner of the row, the agent
  -> the listener of each running instance hears it
  -> it is handed to what follows that user on the instance, and to nothing else
  -> the feed of each open page of that user:
       the page shows that subject  -> the page asks for itself again
       a part of the page ticks     -> the part is read again, by owner, and written to the page
```

| Step | What does it |
|---|---|
| The database says that a row of a user changed, when the change is committed. | The triggers of `AddLiveSignals`. See "What the database says". |
| One listener of the instance hears it, and hands it on by the owner of the row. | `LiveListener` and `LiveSignals` in `Platform`. See "Signals". |
| The page that is open follows the signals of its user. | `LiveFeed` in the Web UI, one for each page. See "The feed of a page". |
| The page asks for itself again, or a part of it is written again. | `LiveParts.razor`, over the connection of the page. See "The one push channel". |

**A signal holds nothing of what changed.** It says the subject, the user, and the agent. What changed is asked for by owner, as every page asks: see [ownership.md](ownership.md). So a signal that reached a page of another user would show that user nothing.

## The one push channel

The channel is the connection that the framework keeps for a page with an interactive part, under `/_blazor`. A page that follows has one such part, `LiveUpdates`, so it has one connection, and everything that the application pushes to the browser goes over it.

| Rule | Detail |
|---|---|
| **There is no other channel.** | The application has no hub of its own, no WebSocket endpoint, and no stream of server-sent events, and no script of a page polls. `ComponentRules.cs` reserves the namespaces of the framework's hubs and of server-sent events for nobody, and lists what maps a hub, takes a WebSocket, or sends a stream of events among the methods that no code may call. Code that would add a channel fails `DependencyRuleTests`. |
| **A page makes the connection only when it follows.** | The framework's script is on every page, and connects only for a page that has an interactive part. `LivePageTests` holds the seven pages to having one, and other pages to having none. |
| **Two things go over it.** | That the page is to ask for itself again, and the parts that tick: the steps of a cycle that runs, with what each role of its team does on the screen of an agent, and the position of an agent with its price. Nothing else of the application is interactive. |
| **A page that follows is rendered on the server for each request, as before.** | Its forms are posted to the server with the token of the page, are answered by a request, and are recorded with where the request came from: see [security.md](security.md) and "The audit trail" in [accounts.md](accounts.md). So the page is not written over the connection. It is asked for again, and the browser changes what differs: the buttons, the tables, and the count in the bar are those of now, and nothing is drawn twice by two pieces of code. |
| **The part is written with the page.** | What it shows is in the page as the server writes it, so the page reads the same before its connection is made, and for a browser that runs no script, which then shows the page as it was written. |
| **The part of a page stays when the page asks for itself again.** | `LiveUpdates` gives it the path of the page as its key. On another page it is another part, with a feed of its own. |
| **A page that answers a form asks for itself again on nothing.** | What the form answered, such as why an agent was not started, would be gone from it. `LiveView.Of` leaves the subjects out for a request that is not a GET. Its parts are written all the same. |
| **A page that follows has no field to type in.** | A page that is asked for again is written anew around its user, and what they had typed would be lost. The seven pages have buttons and links. `LivePageTests` holds them to it. |

## Signals

```csharp
public interface ILiveSignals
{
    long Mark();
    IDisposable Follow(Guid userId, long since, Action<LiveSignal> heard);
    void Raise(Guid userId, Guid? agentId, LiveSubject subject);
}

public readonly record struct LiveSignal(LiveSubject Subject, Guid? AgentId);
```

| Rule | Detail |
|---|---|
| **A signal is of one user.** | `Follow` takes the user, and a signal is handed to what follows that user, and to nothing else. The user of a signal is the owner of the row that changed, which the row says itself, or the owner that whoever raises it names. |
| **The database says what changed in it, whatever wrote it.** | A statement of a component, a job, a migration, or a person at the database: the trigger of the table says it. So no component says that it wrote something, and none can forget to. The orders are written in many places, and none of them was touched for this. |
| **It says so when the change is committed.** | PostgreSQL delivers a notification when its transaction commits, and never for one that is rolled back. What a page then reads is there to be read. |
| **What the database does not hold is raised.** | `Raise` is for the step of a cycle that runs, and for a role of it that is over, which are in memory until the cycle is recorded: `LiveSubject.CycleStep`. It reaches what follows on this instance, and never throws: whoever raises is running a cycle. |
| **Signals are counted, and a page says the count that it read at.** | `Mark` says how many signals the instance has heard. A page asks before it reads what it shows, and its feed hands the number to `Follow`: the signals of its user that were heard since are told at once. So nothing is missed between the writing of a page and the making of its connection, which takes a moment. A clock would not do for it: the database says a change at its own time. |
| **What cannot be told any more is said as missed.** | Signals are kept for 30 seconds, and 10,000 at the most. What follows with an older count, with a count from before the listener listened, or with a count that is not of this instance, is told `LiveSubject.Missed`, and asks for everything that it shows again. |
| **A follower is called by whoever raised the signal.** | So it returns at once: the feed of a page puts the signal in a line of its own. What a follower throws is logged as an error, and reaches neither the other followers nor whoever raised. |

### What the database says

The triggers are in [`LiveRules.cs`](../tests/Ecnanib.Tests/LiveUpdates/LiveRules.cs), which is the source of truth, and `LiveRuleTests` holds the database to it: the triggers are no part of the model, so nothing generates them, and a migration that rebuilds a table would drop its trigger without a word. See "Migrations" in [persistence.md](persistence.md).

| Table | Subject | What is said | What is not |
|---|---|---|---|
| `agents` | `Agent` | An agent that is created or deleted, and a change of its state, its mode, or a setting. | `roles_reassigned_at`, which is on the form of the agent alone. |
| `positions` | `Holdings` | A position that is opened or closed, and what it holds, its entry price, and its protection. | `alerted_at` and `funding_due_at`. |
| `orders` | `Holdings` | An order that is written, and what becomes of it. | `checked_at`, which is written each time the exchange is asked about an order that rests. |
| `decision_cycles` | `Cycle` | A cycle that is recorded. | |
| `equity_snapshots` | `Equity` | The equity of an agent that is recorded: what it made or lost has changed, or what its open position is worth has. | A row that goes with its agent: the table of the agents says that. A row is never changed. |
| `notifications` | `Notification` | A notification that is raised, read, or removed. | `email_pending` and `emailed_at`. |
| `sessions` | `Session` | A session that ends, however it ends. | A session that begins, and `last_active_at`, which is written with every minute of use. |

- **One function says it for every table:** `live_signal()`. A trigger names its subject, the column that says the agent, and the columns whose change alone is said to nobody.
- **A statement that writes a row as it is says nothing,** and neither does one that changes only what is routine.
- **What is said is the subject, `user_id`, and the agent,** on the channel `ecnanib_live`. PostgreSQL says the same thing once for one transaction, so an account that is deleted says each of its agents once.
- **Transactions that say a change commit one after the other.** PostgreSQL says things in the order of the commits, so it lets one transaction that notifies commit at a time, across the server. A commit is short, and a cycle writes a few such rows. Whether that holds for the cycles of 500 agents at one close is for the load test to confirm (NFR-PRF-3).
- **A subject is a member of `LiveSubject`, by its name.** Renaming one needs a migration.
- **A table that a page comes to follow** is listed in `LiveRules.cs`, and given its trigger by a migration that is written by hand, as `AddLiveSignals` is.

### The listener

| Rule | Detail |
|---|---|
| **Each running instance listens for its own pages.** | A page is served by the instance that its connection is to. So the listener is no job: a job runs on the one instance that holds the lock of the background work. See [background-work.md](background-work.md). |
| **It listens on a session of its own,** which stays open. | Not one of the pool, which would be handed to someone else. It asks the session every 15 seconds whether it is still there, so a connection that broke without saying so is found. |
| **It starts with the first page that follows,** and not with the application. | An instance that serves no such page keeps no connection for it. |
| **A database that is away is waited for.** | The listener tries again every 2 seconds, and says so once, as a warning. Nothing of the application stops for it: the pages are served as before, and follow nothing until it is back. |
| **What was said meanwhile is told as missed.** | Each time the listener begins to listen, every follower is told `Missed`. So the first page after a start, and every open page after a loss, asks for itself again once. |

The log, under `Ecnanib.Platform.LiveUpdates.LiveListener`:

```
This instance hears the changes that the database says, for the pages that follow them
The changes that the database says cannot be heard, so the open pages follow nothing until they can. This instance tries again every 2 seconds
The changes that the database says are heard again, so the open pages follow them again
```

These are events of the instance. The listener begins with the request of whoever asked for the first page that follows, and nothing of that request flows into it: its events carry no `RequestId` and no `UserId` (NFR-LOG-3). `LiveListenerTests` holds that.

## The feed of a page

`LiveFeed` keeps one page up to date. `LiveFeeds` opens it for the user of the connection of the page, with what the page follows: `LiveView`.

| Rule | Detail |
|---|---|
| **It is of the user of the connection.** | `SignedInUser.IdOf` says who, from the user that the connection was signed in as. Nothing of a page says whose it is. |
| **What it shows, it asks for by owner.** | The cycles that run through `ICycleProgress`, and the position through `IOpenPositions`, each for that user. An agent of another user is not found, whatever a page says that it follows. |
| **A page of one agent is told nothing of another.** | A signal that names another agent is left alone. One that names none is about none, or about all of them, and a page of every agent follows each. |
| **The page asks for itself again a moment after a change that it shows,** and at most once a second. | It waits 150 milliseconds for what else changes, so a burst is one request: an order that is executed writes its row, its position, and the state of its agent. A change that comes within a second of a request is asked for when the second is over. |
| **The parts that tick are written at once.** | A step of a cycle, what a role of it does, a position, and each price that the watch reads. |
| **For a page that shows the team at work, it shows a cycle that has ended for 5 seconds,** and reads the cycles again when they are over. | The screen of an agent says so with `LiveShows.Team` (FR-ANI-1). `ICycleProgress` keeps a cycle that has ended for `RunningCycle.KeptAfterEnd`, with how each role ended, and nothing says when that is over. So the feed sets one timer for it, on its own clock, from when the cycle ended: then it reads the cycles again, and the page shows that the team is at rest. A cycle that had ended as the page was written is shown for the rest of its seconds. The feed leaves out a cycle whose seconds are over, whatever it is told. |
| **A page that does not show the team is told the cycles that run,** and none that has ended. | The list of the cycles and the page that runs a test cycle, as before. They wait for no seconds to be over. |
| **For a page that values what its user holds, it follows the prices of that.** | The dashboard says so with `Values`: its figures count each open position at the price that the watch has, and the watch has the price of what someone follows. The feed asks for the symbols that the agents of its user hold a position in, by owner, with `IOpenPositions.HeldByUserAsync`, when it begins and each time that the database says that what they hold changed, and follows the price that each is valued at: the mark price on Futures, and the price that the symbol last traded at on Spot. A price of something that is no longer held is let go of. Two agents that hold the same symbol are one price. |
| **For a page of one agent that values what the agent holds, it follows no other price** than the one of the position of that agent. | The screen of an agent says so with `Values` as well (FR-RPT-2): its equity, its net result, and its return count the open position at the price that the watch has. The feed follows that price already, for the part of the page that says it, and awaits its first read when the agent holds a position. The prices of what the other agents of its user hold are not followed for it: a price that is followed is read from Binance once a second. A page of an agent that holds nothing awaits no price: no figure of it is in one. |
| **The first read of such a price has the page ask for itself again,** and a later read asks for nothing. | The page was written without the price, or with an older one, and is written again with it, a moment later, as after any change. This holds for every price that the feed begins to follow, whether or not the watch had it when the page was written: another page may have begun to follow it in between. A price that is read again is on the page the next time that the page is written. Nothing of it is written over the connection. A page that answers a form values nothing, as it follows nothing. |
| **It asks whether the session of its connection stands,** when a session of its user ended, when something was missed, and once a minute. | See "The session of a connection". When it does not, nothing more is written, what was shown is taken back, and the browser loads the page anew, which leads to the page that signs in. |
| **Nothing is asked of a page whose connection is down.** | A browser that sleeps loses its connection for a while, and the framework keeps the page for it. `LiveCircuit` says whether the connection is up. What the page owes, it does when the connection is back. |
| **A connection that is lost is made again by the framework.** | While it is lost, the framework's script says so in a dialog over the page, and tries again by itself. A connection that comes back while the server still keeps the page goes on where it was. Otherwise, as after the application was started again, the browser loads the page anew once the application answers, and the page follows again. The dialog is the framework's own, with its own words, and it shows under the content security policy of the pages as it is. |
| **A connection that is made again is signed in again.** | With another user, or another session, the feed stops: it is of the one that it was opened for. |
| **An error stops the feed, and is recorded under a reference.** | `LiveErrors` takes it, calls `UnhandledErrors.Report`, and says the reference on the page in place of the part, and nothing of the error (NFR-LOG-6). The rest of the page is as it was written. See "Unhandled errors" in [logging.md](logging.md). |
| **It does one thing after the other.** | A signal, a price, and a timer each wait their turn, on a line of the feed's own. |

## The pages

| Page | It asks for itself again on | Written to it over the connection |
|---|---|---|
| `/dashboard` | `Agent`, `Holdings`, `Cycle`, `Notification`, and the first read of each price of what its user holds | |
| `/agents` | `Agent`, `Holdings`, `Notification` | |
| `/agents/<id>/state` | `Agent`, `Holdings`, `Cycle`, `Equity`, `Notification`, and the first read of the price of its position | The team at work as a pipeline, with the cycles of the agent that run and the one that ended within the last 5 seconds, or that its team is at rest, and its position with its price. |
| `/agents/<id>/cycles` | `Cycle`, `Agent`, `Notification` | The cycles of the agent that run. |
| `/agents/<id>/cycles/<cycle>` | `Holdings`, `Agent`, `Notification` | |
| `/agents/<id>/test-cycle` | Nothing: its form waits for the cycle, and the page would lose what the form answers. | The cycles of the agent that run, the test cycle among them, also while the form waits. |
| `/notifications` | `Notification`, `Agent` | |

- **A page follows `Notification` for the count in the bar,** which is on every page. On a page that does not follow, the count is as it was when the page was written.
- **A page says what it follows with `<LiveUpdates />`,** and reads `ILiveSignals.Mark` before it reads what it shows.

**The page of the state of an agent is the screen of the agent** (FR-RPT-2): beside its state and its controls it shows the performance figures, the equity curve, the trades, and the latest decisions of the agent, which are written with the page, and are those of now each time that it asks for itself again. It says what its team does now also when the team does nothing: with `LiveShows.Team`, the part of the cycles shows the team at work as a pipeline, each role with its model and with what it does, says that the team is at rest while no cycle of the agent runs, and shows a cycle that has ended for 5 seconds (FR-ANI-1). The page hands the part the roles with their models, as it read them: `LiveView.Team`. What each role does is asked of the cycles. See "The team at work" in [agent-screen.md](agent-screen.md).

**A cycle that runs** says what started it, which candle it is for, when it started, and the steps that it has come to so far, in their order. The last one is what it does now, and says since when. A step that it will not come to is not listed: which steps a cycle comes to is its decision. Nothing of what a model answered is there: the timeline of the cycle shows that, once it is recorded. See "The progress of a cycle" in [cycles.md](cycles.md).

**The position** of an agent says what it holds, the price that it was entered at, and since when, and on the Futures market its side. It says its stop-loss and its take-profit as they rest on the exchange, and says so where none rests: the price alone protects nothing. Under it is the price that the position is valued at, with when Binance was asked, and what the position would make at that price, before fees and funding, in words: a loss is said as a loss, and relies on no colour (FR-THM-5). An agent that holds nothing says so, with the price of its symbol. See "The position on a page" in [orders.md](orders.md) and "Following a price" in [exchange.md](exchange.md).

**The page never waits for Binance.** It is written with the price that the watch has, when another page follows it already, and otherwise says that the price is not known at the moment. The connection brings it within a second.

**The dashboard** values every open position of its user by the same rule, and adds them into the equity of each mode: see [dashboard.md](dashboard.md). It has no part that ticks. A figure that a price is in is not known while the watch has no such price, and says so. The feed of the page follows the prices of what its user holds, and the page asks for itself again when each is first read, so a dashboard that was written with a figure that is not known shows it within a second or two. After that its figures are of the moment that the page was written, and say the time of their prices.

## Pausing

A page that changes by itself can be stopped by whoever reads it (NFR-USA-1; WCAG 2.1, 2.2.2): someone who reads slowly, who reads with a screen reader, or who is put off by what moves. Each of the seven pages says under its heading that it follows what changes, with a link that pauses it, and a paused page says since when it is as it is, with a link that has it follow again.

| | A page that follows | A page that is paused |
|---|---|---|
| Address | As before. | The same, with `?live=paused`. |
| Under its heading | "This page follows what changes, and updates by itself." and "Pause live updates". | "Live updates are paused. This page shows what was so at …, and does not change by itself." and "Resume live updates". |
| Its part for a connection | One, as before. | None: the page makes no connection, and nothing is followed for it. |
| Its parts that tick | Written with the page, and again over the connection, inside `<div class="live-parts">`. | Written with the page, once, inside `<div class="live-parts" data-live="paused">`. |

| Rule | Detail |
|---|---|
| **It is a choice of the address, and nothing is stored for it.** | `LivePages.PauseKey` and `LivePages.Paused`. It holds for the page that is open, until its user has it follow again or opens the page anew: another screen follows, and so does this one from the bar. A choice that there is not, such as `?live=on` or the choice made twice, has no page, as an address with a page number that is none has not. Each of the seven pages answers that itself. |
| **The link leads to the same page, with every other choice of the address as it is.** | `LivePages.AddressOf`: the page of a list, and what else the address says, each written anew, and the pause after them. Only what follows the question mark changes, so the browser keeps the place and the focus, and the link is then the other one. |
| **What the page says of itself is said to a screen reader when it changes.** | `LivePause.razor` writes it into a part with `role="status"`, which is there in both cases: so following the link is answered in words. |
| **A paused page is written as every page is before its connection is made.** | `LiveUpdates.razor` writes its parts without the connection, which is how the server writes them for each request in any case: the feed is opened, read once, and closed, and it follows no signal, no price, and no clock. So nothing new reads a cycle or a position for it. A page that shows no part writes nothing in their place. |
| **The part stands in an element of its own.** | The framework takes an element that holds a part with a connection for another kind of element than one that holds none, and puts a new one in its place when a page goes from the one to the other. Without an element of its own that would be the content of the page, and the link that pauses, which has the focus at that moment, would lose it. |
| **What its user does on a paused page keeps it paused.** | A form of the page that leads back to it, and the other pages of its list, carry the choice: `LivePages.Kept`, on the page of the state of an agent, the dashboard with its checklist, and the notifications. So marking a notification as read does not have the page follow behind the back of its user. A link to another screen carries none. |
| **Nothing on it moves for as long as a state lasts.** | The dot of a role that works stands still inside `[data-live="paused"]`: the cycle ran when the page was written, and the page does not say when it ended. See [motion.md](motion.md). |
| **A paused page reads no price.** | A price is read for a page that follows it, and the page never waits for Binance: so a paused page says the price that the watch had, when another page followed it, and says otherwise that the price is not known. The two pages that show prices, the dashboard and the screen of an agent, say so beside the link. |
| **The count of the notifications in the bar is as it was when the page was written,** | as on every page that does not follow. |

## The session of a connection

A page that is rendered for each request is checked with each request. A connection is signed in when it is made, and is not asked for again. And a page that follows makes requests that its user did not. Accounts answers for both, with `IConnectedSession`: see "The session of a connection" in [accounts.md](accounts.md).

| Rule | Detail |
|---|---|
| **The feed asks whether its session stands.** | When the database says that a session of its user ended, and once a minute, for a session that was left alone for the period of inactivity. Asking is no activity. |
| **What a page does by itself is no activity of its user.** | Its connection, and the request with which it asks for itself again, which its feed announces first. So a page that follows does not keep the session of a browser alive that nobody sits at (NFR-SEC-1). |
| **A session that ended ends for the page.** | Signing out, a new password, the deletion of the account, and the period of inactivity each stop the feeds of that session, within moments or within the minute, and the browser lands on the page that signs in. |

## Within two seconds

| What changed | How it reaches the screen | Takes |
|---|---|---|
| A row of the user: the state of an agent, a position, an order, a cycle that is recorded, an equity that is recorded, a notification. | The commit, the notification, the listener, the feed, one request for the page, and the browser changes what differs. | The 150 milliseconds that the feed waits, and the request. After a request, a second at the most, and the request. |
| A step of a cycle that runs, and what a role of it does. | Raised in memory, read from memory, and written over the connection. | At once. |
| A price. | Read once a second for all who follow it, and written over the connection. | A second, while Binance answers and the limit has room. |

`LiveFeedTests` holds the feed to its 150 milliseconds and its second on a clock that the test moves, and `PriceWatchTests` holds the watch to its second.

## One instance

Version 1 targets a single running instance: see [background-work.md](background-work.md). With a second one:

| What | With a second instance |
|---|---|
| What the database says | Reaches the pages of both: each has its listener. |
| The steps of a cycle that runs | Reach the pages of the instance that runs the cycle. A page of the other shows the cycle once it is recorded. |
| A price | Each instance reads for its own pages, so a price that both follow is read twice a second. |
| The prices of a dashboard | Are followed by the instance of its connection. A request for the page that the other instance takes is written with the prices that that instance has, which may be none. |
| That a page asks for itself again | Is announced to the instance of its connection. A request that the other instance takes counts as activity. |

## Testing

| Test class | What it holds |
|---|---|
| `LiveSignalTests` | A follower hears the signals of its user alone. What begins to follow is told what was heard since it read, or that it missed something. A follower that fails fails nobody. |
| `LiveListenerTests` | The whole way, on PostgreSQL: a write, the trigger, the listener, and the follower, for two users. A change that is rolled back is said to nobody, and neither is a routine write. |
| `LiveRuleTests` | The triggers of the database are those of `LiveRules.cs`. |
| `LiveFeedTests` | What a feed asks of its page, with a stand-in for the page: when it asks again, what it writes, whom it tells nothing, when it stops. Which prices it follows for a page that values what its user holds, and that the first read of each has the page ask again. That the screen of one agent has the one price of its position followed, and that an equity which is recorded has the page of its agent ask again. That a cycle which has ended is shown on the screen of its agent for 5 seconds, from when it ended, and on no page that does not show the team. |
| `LivePageTests` | The seven pages carry one part each, and no field. What a page says of a cycle that runs and of a position, as it is written. The team at work on the screen of an agent, as it is written: see "Testing" in [agent-screen.md](agent-screen.md). That each of the seven offers to pause under its heading and no other page does, that a paused page carries no part for a connection, says when it was written, and is written with what its parts showed, that the choice stays beside the other choices of an address, through the pages of a list, and through a form that leads back to the page, and that a choice which there is not is not found. |
| `ConnectedSessionTests` | When the session of a connection stands, and which requests are no activity. |
| `CycleProgressTests`, `OpenPositionTests` | The steps of a cycle as it runs, and the position of an agent, each for its user alone. |
| `PriceWatchTests`, `RequestLimitTests` | One read a second for all who follow a price, none without a follower or without room. |

No test of these drives a browser. `AccessibilityBrowserTests` does, when a run is told to, and opens the pages that follow among every other, paused and following: see [accessibility.md](accessibility.md). What the browser does with a page that asks for itself again is the framework's, and is looked at by hand:

1. `docker compose up -d`, and run the application.
2. Sign in, create an agent, and open the page of its state.
3. Change its row at the database, such as `UPDATE agents SET state = 'Paused' WHERE id = '...'`: the page shows the state and the controls of it within two seconds, with no reload.
4. The network panel of the browser shows one WebSocket, to `/_blazor`, and no request that repeats by itself.
5. Follow "Pause live updates": the WebSocket closes within moments, the page says since when it is as it is, and a change at the database changes nothing on it. Mark a notification as read on it: the page comes back paused. Follow "Resume live updates": the page follows again.

## Not built yet

- Saying a change without a turn at the commit: see "What the database says". A table of what changed, which the listener reads, would say it without one, and is not built.
- A stream of prices from Binance. A price that is followed is read once a second, which costs of the request budget: see "Following a price" in [exchange.md](exchange.md).
- The steps of a cycle that runs, on a page that another instance serves, and a cycle that still waits for its turn in the queue of a close: it says its steps from the moment that its turn comes.
- Pages that follow beyond the seven: the experience screens, and the pages of the keys. The count of the notifications in the bar follows only on a page that follows.
- Figures of the dashboard that move with the price: its equity and what its positions would make are of the moment that the page was written, and are worked out anew when something that it shows changes. And what changes a figure and is said by no table that a page follows: a fee that is valued later, funding, and the UTC day that begins. See "Not built yet" in [dashboard.md](dashboard.md).
- A form that is answered over the connection. Every form is posted to the server and answered by a request.
- Announcing a request to another instance: see "One instance".
- A page that is loaded anew after its connection could not be made again counts as activity of its user: the browser asks for it as for any page.
- A pause that holds on every page, or that an account keeps. It is of the page that is open.
- A price on a paused page that no other page follows. It says that the price is not known.
- A notice of the application's own for a connection that is lost. The dialog is the framework's, and it covers the page while the connection is tried again, though the links and the forms of the page need no connection.
