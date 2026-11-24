# Live updates

Agent status, positions, prices, and cycle progress update in the UI within 2 seconds, without a manual refresh (FR-NTF-2). Live UI updates use one push channel from the application to the browser (NFR-ARC-6). A user receives updates only for their own agents.

What is built is that, for the pages that show those things: the list of the agents, the pages of the state, the cycles, and a cycle of an agent, the page that runs a test cycle, and the page of the notifications. A page that follows is still rendered on the server for each request, with its forms as before. It carries one part for which it makes a connection, and over that connection the application says two things: that something which the page shows has changed, upon which the page asks for itself again, and the values that tick, which are written to the page as they change.

The signals are in `src/Ecnanib/Platform/LiveUpdates`, with the subjects in `src/Ecnanib/Shared/LiveSubject.cs` and the triggers of the database in the migration `AddLiveSignals`. What a page does with them is in `src/Ecnanib/WebUi/Live`. What it asks of the components is `IConnectedSession` in `src/Ecnanib/Accounts`, `ICycleProgress` and `IOpenPositions` in `src/Ecnanib/DecisionEngine`, and `IExchangePriceWatch` in `src/Ecnanib/ExchangeGateway`. The rules below are enforced by the tests in `tests/Ecnanib.Tests/LiveUpdates`, by `ConnectedSessionTests` in `tests/Ecnanib.Tests/Accounts`, by `CycleProgressTests` and `OpenPositionTests` in `tests/Ecnanib.Tests/DecisionEngine`, by `PriceWatchTests` and `RequestLimitTests` in `tests/Ecnanib.Tests/ExchangeGateway`, and by the reserved namespaces of [`ComponentRules.cs`](../tests/Ecnanib.Tests/Architecture/ComponentRules.cs).

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
| **A page makes the connection only when it follows.** | The framework's script is on every page, and connects only for a page that has an interactive part. `LivePageTests` holds the six pages to having one, and other pages to having none. |
| **Two things go over it.** | That the page is to ask for itself again, and the parts that tick: the steps of a cycle that runs, and the position of an agent with its price. Nothing else of the application is interactive. |
| **A page that follows is rendered on the server for each request, as before.** | Its forms are posted to the server with the token of the page, are answered by a request, and are recorded with where the request came from: see [security.md](security.md) and "The audit trail" in [accounts.md](accounts.md). So the page is not written over the connection. It is asked for again, and the browser changes what differs: the buttons, the tables, and the count in the bar are those of now, and nothing is drawn twice by two pieces of code. |
| **The part is written with the page.** | What it shows is in the page as the server writes it, so the page reads the same before its connection is made, and for a browser that runs no script, which then shows the page as it was written. |
| **The part of a page stays when the page asks for itself again.** | `LiveUpdates` gives it the path of the page as its key. On another page it is another part, with a feed of its own. |
| **A page that answers a form asks for itself again on nothing.** | What the form answered, such as why an agent was not started, would be gone from it. `LiveView.Of` leaves the subjects out for a request that is not a GET. Its parts are written all the same. |
| **A page that follows has no field to type in.** | A page that is asked for again is written anew around its user, and what they had typed would be lost. The six pages have buttons and links. `LivePageTests` holds them to it. |

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
| **What the database does not hold is raised.** | `Raise` is for the step of a cycle that runs, which is in memory until the cycle is recorded: `LiveSubject.CycleStep`. It reaches what follows on this instance, and never throws: whoever raises is running a cycle. |
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
| **The parts that tick are written at once.** | A step of a cycle, a position, and each price that the watch reads. |
| **It asks whether the session of its connection stands,** when a session of its user ended, when something was missed, and once a minute. | See "The session of a connection". When it does not, nothing more is written, what was shown is taken back, and the browser loads the page anew, which leads to the page that signs in. |
| **Nothing is asked of a page whose connection is down.** | A browser that sleeps loses its connection for a while, and the framework keeps the page for it. `LiveCircuit` says whether the connection is up. What the page owes, it does when the connection is back. |
| **A connection that is lost is made again by the framework.** | While it is lost, the framework's script says so in a dialog over the page, and tries again by itself. A connection that comes back while the server still keeps the page goes on where it was. Otherwise, as after the application was started again, the browser loads the page anew once the application answers, and the page follows again. The dialog is the framework's own, with its own words, and it shows under the content security policy of the pages as it is. |
| **A connection that is made again is signed in again.** | With another user, or another session, the feed stops: it is of the one that it was opened for. |
| **An error stops the feed, and is recorded under a reference.** | `LiveErrors` takes it, calls `UnhandledErrors.Report`, and says the reference on the page in place of the part, and nothing of the error (NFR-LOG-6). The rest of the page is as it was written. See "Unhandled errors" in [logging.md](logging.md). |
| **It does one thing after the other.** | A signal, a price, and a timer each wait their turn, on a line of the feed's own. |

## The pages

| Page | It asks for itself again on | Written to it over the connection |
|---|---|---|
| `/agents` | `Agent`, `Holdings`, `Notification` | |
| `/agents/<id>/state` | `Agent`, `Holdings`, `Notification` | The cycles of the agent that run, and its position with its price. |
| `/agents/<id>/cycles` | `Cycle`, `Agent`, `Notification` | The cycles of the agent that run. |
| `/agents/<id>/cycles/<cycle>` | `Holdings`, `Agent`, `Notification` | |
| `/agents/<id>/test-cycle` | Nothing: its form waits for the cycle, and the page would lose what the form answers. | The cycles of the agent that run, the test cycle among them, also while the form waits. |
| `/notifications` | `Notification`, `Agent` | |

- **A page follows `Notification` for the count in the bar,** which is on every page. On a page that does not follow, the count is as it was when the page was written.
- **A page says what it follows with `<LiveUpdates />`,** and reads `ILiveSignals.Mark` before it reads what it shows.

**A cycle that runs** says what started it, which candle it is for, when it started, and the steps that it has come to so far, in their order. The last one is what it does now, and says since when. A step that it will not come to is not listed: which steps a cycle comes to is its decision. Nothing of what a model answered is there: the timeline of the cycle shows that, once it is recorded. See "The progress of a cycle" in [cycles.md](cycles.md).

**The position** of an agent says what it holds, the price that it was entered at, and since when, and on the Futures market its side. It says its stop-loss and its take-profit as they rest on the exchange, and says so where none rests: the price alone protects nothing. Under it is the price that the position is valued at, with when Binance was asked, and what the position would make at that price, before fees and funding, in words: a loss is said as a loss, and relies on no colour (FR-THM-5). An agent that holds nothing says so, with the price of its symbol. See "The position on a page" in [orders.md](orders.md) and "Following a price" in [exchange.md](exchange.md).

**The page never waits for Binance.** It is written with the price that the watch has, when another page follows it already, and otherwise says that the price is not known at the moment. The connection brings it within a second.

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
| A row of the user: the state of an agent, a position, an order, a cycle that is recorded, a notification. | The commit, the notification, the listener, the feed, one request for the page, and the browser changes what differs. | The 150 milliseconds that the feed waits, and the request. After a request, a second at the most, and the request. |
| A step of a cycle that runs. | Raised in memory, read from memory, and written over the connection. | At once. |
| A price. | Read once a second for all who follow it, and written over the connection. | A second, while Binance answers and the limit has room. |

`LiveFeedTests` holds the feed to its 150 milliseconds and its second on a clock that the test moves, and `PriceWatchTests` holds the watch to its second.

## One instance

Version 1 targets a single running instance: see [background-work.md](background-work.md). With a second one:

| What | With a second instance |
|---|---|
| What the database says | Reaches the pages of both: each has its listener. |
| The steps of a cycle that runs | Reach the pages of the instance that runs the cycle. A page of the other shows the cycle once it is recorded. |
| A price | Each instance reads for its own pages, so a price that both follow is read twice a second. |
| That a page asks for itself again | Is announced to the instance of its connection. A request that the other instance takes counts as activity. |

## Testing

| Test class | What it holds |
|---|---|
| `LiveSignalTests` | A follower hears the signals of its user alone. What begins to follow is told what was heard since it read, or that it missed something. A follower that fails fails nobody. |
| `LiveListenerTests` | The whole way, on PostgreSQL: a write, the trigger, the listener, and the follower, for two users. A change that is rolled back is said to nobody, and neither is a routine write. |
| `LiveRuleTests` | The triggers of the database are those of `LiveRules.cs`. |
| `LiveFeedTests` | What a feed asks of its page, with a stand-in for the page: when it asks again, what it writes, whom it tells nothing, when it stops. |
| `LivePageTests` | The six pages carry one part each, and no field. What a page says of a cycle that runs and of a position, as it is written. |
| `ConnectedSessionTests` | When the session of a connection stands, and which requests are no activity. |
| `CycleProgressTests`, `OpenPositionTests` | The steps of a cycle as it runs, and the position of an agent, each for its user alone. |
| `PriceWatchTests`, `RequestLimitTests` | One read a second for all who follow a price, none without a follower or without room. |

No test drives a browser. What the browser does with a page that asks for itself again is the framework's, and is looked at by hand:

1. `docker compose up -d`, and run the application.
2. Sign in, create an agent, and open the page of its state.
3. Change its row at the database, such as `UPDATE agents SET state = 'Paused' WHERE id = '...'`: the page shows the state and the controls of it within two seconds, with no reload.
4. The network panel of the browser shows one WebSocket, to `/_blazor`, and no request that repeats by itself.

## Not built yet

- Saying a change without a turn at the commit: see "What the database says". A table of what changed, which the listener reads, would say it without one, and is not built.
- A stream of prices from Binance. A price that is followed is read once a second, which costs of the request budget: see "Following a price" in [exchange.md](exchange.md).
- The steps of a cycle that runs, on a page that another instance serves, and a cycle that still waits for its turn in the queue of a close: it says its steps from the moment that its turn comes.
- Pages that follow beyond the six: the dashboard (FR-RPT-1), the trades and the equity of an agent (FR-RPT-2), the experience screens, and the pages of the keys. The count of the notifications in the bar follows only on a page that follows.
- Animation of what changes (FR-ANI-1 to FR-ANI-7). A part that changes is written anew, as text.
- A form that is answered over the connection. Every form is posted to the server and answered by a request.
- Announcing a request to another instance: see "One instance".
- A page that is loaded anew after its connection could not be made again counts as activity of its user: the browser asks for it as for any page.
- A notice of the application's own for a connection that is lost. The dialog is the framework's, and it covers the page while the connection is tried again, though the links and the forms of the page need no connection.
