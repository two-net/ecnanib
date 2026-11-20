# Notifications

In-app notifications cover: position opened or closed; agent halted or needing attention; key invalid; model unavailable; order or protection failure; reconciliation mismatch; and liquidation warning (FR-NTF-1).

What is built is what seven events need. The first is a failure of the protection of a position: every open position has a stop-loss order resting on the exchange, and if the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user (FR-RSK-7). The second is an entry that is skipped: if the quote balance is below what the plan needs, the entry is skipped and the user is notified (FR-BIN-S3), and so it is when the Futures wallet lacks the margin the plan needs (FR-BIN-F6). The third is an agent that needs attention because the account of its user at Binance conflicts with how it trades: the user is told what to change (FR-BIN-F2). The fourth is a liquidation warning: the system checks the margin state of open positions at each reconciliation and alerts the user when a position approaches liquidation, and it never adds margin on its own (FR-BIN-F5). The fifth is a reconciliation mismatch: on any mismatch the exchange is treated as the source of truth, records are corrected, and the mismatch is logged and shown (FR-BIN-7), which is how a user sees what they did at Binance by hand in the records of their agent (FR-BIN-8). The sixth is an agent that is halted: when its loss for the UTC day reaches the daily loss limit, or its drawdown from peak equity reaches the drawdown limit, the system closes its position and halts it (FR-RSK-8). The seventh is a role of an agent that the service gave another model by strength: an agent can opt in to automatic role reassignment, and each change is logged and notified (FR-TEAM-10). Each is a notification, which is stored for its user and shown on the page of the agent that it is about.

The code is in `src/Ecnanib/Notifications`: `Notification`, `INotifications`, and `StoredNotifications`. The page is `AgentStatus.razor` in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by `NotificationTests` in `tests/Ecnanib.Tests/Notifications`, and by `ProtectiveStopTests`, `SpotEntryTests`, `FuturesEntryTests`, `FuturesProtectionTests`, `LiquidationWarningTests`, `ReconciliationTests`, and `LossLimitHaltTests` in `tests/Ecnanib.Tests/DecisionEngine`, and by `RoleReassignmentTests` in `tests/Ecnanib.Tests/Agents`, for when one is raised.

What raises the notification is in [orders.md](orders.md), and for a role that was reassigned in [agents.md](agents.md). The email that the same component sends for an account is in [accounts.md](accounts.md).

## The interface

```csharp
public interface INotifications
{
    Task RaiseAsync(Guid userId, NotificationType type, Guid? agentId, string content, CancellationToken cancellationToken);
    Task<bool> RaiseOnceAsync(Guid userId, NotificationType type, Guid agentId, string content, TimeSpan within, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationView>> UnreadOfAgentAsync(HttpContext request, Guid agentId);
}
```

| Rule | Detail |
|---|---|
| **A notification belongs to one user** | `RaiseAsync` takes the user by id: it is asked by background work, where nobody is signed in, and the id is the owner of the row that the notification is about. `UnreadOfAgentAsync` reads for the user of the session, and asks for the rows by owner. See [ownership.md](ownership.md). |
| **What happens again and again is said once in a time** | `RaiseOnceAsync` raises nothing when the user has a notification of the same type about the same agent that was raised within the time, read or not, and says whether it raised one. It is for what repeats while nothing changes. The page of an agent shows the latest 20 that are not read, and nothing marks one as read yet, so one for each time would leave no room for the others. Two that ask at the same moment may both raise one. |
| **It says what happened, in plain words** | The content is the service's own text, in 500 characters at most: what became of a position, or how it stands, with its symbol and its mode. Nothing that a user typed is in it, and no secret. |
| **It names its agent by the id alone** | Notifications may not use Agents, so the row does not point at the agent's row. A notification belongs to the account, and stays when the agent is deleted. |
| **It is not read until its user says so** | Nothing marks one as read yet, so every notification about an agent is on the page of the agent. |
| **The log does not repeat it** | The event of a notification that was raised names the user, the agent, and what it is about. |

## What is raised

| Type | When | What it says |
|---|---|---|
| `ProtectionFailure` | The stop-loss of a position could not be placed, and the position was closed at market. | "The stop-loss of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market." |
| `ProtectionFailure` | The stop-loss could not be placed, and the position could not be closed either. It is said once for each time that a position comes to that. | "The stop-loss of the position in BTCUSDT (Demo) could not be placed, and the position could not be closed at market either. It is open with no stop-loss. Close it with "Close position now", or at Binance." |
| `ProtectionFailure` | The take-profit of a position on Futures could not be placed, because Binance refused it, and the position was closed at market. See "The take-profit of Futures" in [orders.md](orders.md). | "The take-profit of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market." |
| `ProtectionFailure` | The take-profit of a position on Futures could not be placed, and the position could not be closed either. Its stop-loss rests again, and its take-profit is given up. | "The take-profit of the position in BTCUSDT (Demo) could not be placed, and the position could not be closed at market either. It is open with its stop-loss, and with no take-profit. Close it with "Close position now", or at Binance." |
| `EntrySkipped` | An entry on Spot was skipped: the account holds less of the asset that the symbol is priced in than the entry needs, as it was read before the entry, or once more after Binance refused the entry. It is said once in a day for an agent. See "The quote balance" in [orders.md](orders.md). | "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT, and the account has 120.5 USDT free. This is said once in a day for an agent." |
| `EntrySkipped` | An entry on Futures was skipped: the wallet has less of that asset available than the margin of the entry at its leverage, read at the same two moments. It is said once in a day for an agent. See "The margin of Futures" in [orders.md](orders.md). | "An entry in BTCUSDT (Demo) was skipped: it needs 600 USDT of margin at a leverage of 2, and the Futures wallet has 120.5 USDT available. This is said once in a day for an agent." |
| `EntrySkipped` | An entry on Futures was skipped: Binance refused to give the symbol isolated margin, or the leverage of the order. It is said once in a day for an agent. | "An entry in BTCUSDT (Demo) was skipped: Binance did not set isolated margin at a leverage of 2 for BTCUSDT. It does not change the margin of a symbol while the account has open orders or a position in it: cancel or close them at Binance. This is said once in a day for an agent." |
| `AttentionRequired` | An entry on Futures was skipped because the account of the user at Binance conflicts with how the agent trades, and the agent waits for its user: the account is in hedge mode or in Multi-Assets mode, or holds a position in the symbol that the agent did not open. It says which, and what to change. See "The account of Futures" in [orders.md](orders.md). | "An entry in BTCUSDT (Demo) was skipped, and the agent waits for you: your Binance Futures account is in hedge mode, and the agent trades in one-way mode. Switch the position mode of the account to one-way at Binance, and resume the agent." |
| `LiquidationWarning` | A position on Futures approaches liquidation: no more than a share of the way from the price that it was entered at to the price that Binance liquidates it at is left, as the margin was read. The agent stays in its state, and no margin is added. It is said once in an hour for an agent. See "The margin of a position" in [orders.md](orders.md). | "The long position in BTCUSDT (Demo) approaches liquidation: its mark price is 52500, and Binance liquidates it at 50000, which is 4.8% away. No margin is added to a position for you: close the position or reduce it, or add margin to it at Binance. This is said once in an hour for an agent." |
| `LiquidationWarning` | The mark price of such a position has reached its liquidation price, or passed it. | "The long position in BTCUSDT (Demo) approaches liquidation: its mark price is 49999.5, and has reached the price that Binance liquidates it at, 50000. No margin is added to a position for you: close the position or reduce it, or add margin to it at Binance. This is said once in an hour for an agent." |

| `ReconciliationMismatch` | The account no longer holds a position of Futures that the records said is open: it was closed at Binance, by its user or by Binance, and by no order of the agent. The records say that it is closed. See "What is corrected" in [orders.md](orders.md). | "Your Binance account no longer holds the long position in BTCUSDT (Demo): it was closed at Binance, and by no order of the agent. The records of the agent now say that it is closed, and what still rests of its stop-loss and its take-profit is taken off the exchange." |
| `ReconciliationMismatch` | The account holds less of a position of Futures than the records said. | "Your Binance account holds 0.005 in the long position in BTCUSDT (Demo), where the records of the agent said 0.02: it was reduced at Binance, and by no order of the agent. The records now say 0.005." |
| `ReconciliationMismatch` | The account holds more of a position of Futures than the records said. The agent manages all of it from then on. | "Your Binance account holds 0.05 in the long position in BTCUSDT (Demo), where the records of the agent said 0.02: it was added to at Binance, and by no order of the agent. The records now say 0.05, the agent manages all of it, and its stop-loss and its take-profit are placed anew for all of it." |
| `ReconciliationMismatch` | The account no longer has what a position of Spot held, once nothing that the agent placed holds it, or has less of it. | "Your Binance account no longer holds what the position in BTCUSDT (Demo) held: it was sold or moved at Binance, and by no order of the agent. The records of the agent now say that the position is closed." And: "Your Binance account holds less than the position in BTCUSDT (Demo) held: 0.006 can be ordered of it, where the records of the agent said 0.01. The rest was sold or moved at Binance, and by no order of the agent. The records now say 0.006." |
| `ReconciliationMismatch` | A stop-loss or a take-profit that the records said rests no longer rested at Binance, with nothing of it executed, and the position is open. | "The stop-loss of the position in BTCUSDT (Demo) no longer rested at Binance, with nothing of it executed, where the records said that it rests. The position is open, so the service places its protection again." |
| `AgentHalted` | An agent reached its daily loss limit while it ran, with unrealized profit and loss, and was halted: its position is closed at market, and the service resumes it when the next UTC day begins (FR-RSK-8). See "Halting an agent" in [risk-guard.md](risk-guard.md). | "The agent reached its daily loss limit in BTCUSDT (Demo): its equity is 949.4 USDT, 5.06% below the 1000 USDT that it had as the UTC day began, and its limit is 5%. It is halted, and its position is closed at market. The service resumes it when the next UTC day begins." |
| `AgentHalted` | An agent reached its drawdown limit while it ran, and was halted until its user resumes it. Where both limits are reached, this is the one that is said. | "The agent reached its drawdown limit in BTCUSDT (Demo): its equity is 778.8 USDT, 22.12% below its peak of 1000 USDT, and its limit is 20%. It is halted, and its position is closed at market. It stays halted until you resume it." |
| `ProtectionFailure` | The position of an agent that a loss limit halted could not be closed at market. It is open as it was, and is closed again with a later evaluation of the loss limits. It is said once in an hour for an agent. | "The position of the halted agent in BTCUSDT (Demo) could not be closed at market. It is open as it was, and the service tries again within minutes. Close it with "Close position now", or at Binance. This is said once in an hour for an agent." |
| `RolesReassigned` | The service gave a role of an agent another model by strength: the agent opted in, runs, and held no position. One for each role that was given another model, with the scores of both models and the data that each is of. It says so when the change gave the Strategist the provider of the Risk Manager, or a provider more than one role. See "Reassigning the roles automatically" in [agents.md](agents.md). | "The Strategist of the agent in BTCUSDT (Demo) now uses claude-b in place of claude-test-model: automatic reassignment by strength is on for it. On Spot 1H, its strength score is +0.5 R from 4 samples of Demo data, against no strength score yet for the model before. It applies from the next cycle, and no role is reassigned again for 7 days." |

A mismatch is said each time that the records were corrected for one: it does not repeat while nothing changes, because the records then say what Binance says.

A role that was reassigned is said once, when it is stored, and names each model by the name its provider gives it, or by its identifier, cut to 40 characters: a name is of any length, and a notification says 500 characters at the most. Where what is said would still be longer, the names are cut further, down to 12 characters, and then the sentence about the providers is left out: no sentence is cut.

An agent that is halted is said once, when it is halted, with its equity, what its loss is counted from, and its limit, each to 2 decimal places. The page of the state of the agent says the state, the cause, and the time beside it (FR-AGT-10).

On Spot the stop-loss is one order of a pair with the take-profit, and a pair that cannot be placed is said as a stop-loss that cannot: the notification is the same. On Futures the two are orders of their own, and the notification says which of them could not be placed. A take-profit that is executed is no failure, and nobody is told.

The page says `ProtectionFailure` as "Order or protection failure", `EntrySkipped` as "Entry skipped", `AttentionRequired` as "Agent needs attention", `LiquidationWarning` as "Liquidation warning", `ReconciliationMismatch` as "Reconciliation mismatch", `AgentHalted` as "Agent halted", and `RolesReassigned` as "Roles reassigned": `AgentPages.Describe`.

A start or a resume that the account at Binance stands in the way of is said on the page that asked, and raises no notification: see "The account of Futures" in [agents.md](agents.md).

That the user could not be told, because the notification could not be stored, is said in the log as an error, and stops nothing of the orders.

## The page

The page of the state of an agent, `/agents/{id}/state`, has a section "Notifications" when its user has not read something about the agent: each notification with what it is about, when it was raised, and what it says, the latest first, 20 at most. What a notification says is written as text, and never as markup (NFR-SEC-4). With nothing to say, the page has no such section.

## The data

The migration `AddOrders` adds the table.

| Table | Columns |
|---|---|
| `notifications` | `id`, `user_id`, `type`, `agent_id`, `content`, `is_read`, `created_at` |

| Rule | Detail |
|---|---|
| **The rows go with the account** | `user_id` points at `users`, and the rows are deleted with the account (FR-ACC-9). |
| **They are in the export** | The section `notifications` of the export of a user's data holds each, with whether it is read (NFR-SEC-9). See [accounts.md](accounts.md). |
| **The type is stored by its name** | Renaming a member of `NotificationType` needs a migration. What raises a notification adds its member when it is built. |

## Not built yet

- The other events of FR-NTF-1: a position that was opened or closed, an agent that needs attention for another cause than its account at Binance or a position without protection, a key that is invalid, and a model that is unavailable. And an agent that runs again after a daily halt, or stays halted after it because its key or its team keeps it.
- The list of all notifications of a user, and marking one as read.
- Email for critical events, for a user who opted in (FR-NTF-3).
- Showing a notification without a reload of the page (FR-NTF-2).
- Removing notifications after the days of `Retention:NotificationDays` (NFR-DAT-5).
