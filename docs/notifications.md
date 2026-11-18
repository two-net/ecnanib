# Notifications

In-app notifications cover: position opened or closed; agent halted or needing attention; key invalid; model unavailable; order or protection failure; reconciliation mismatch; and liquidation warning (FR-NTF-1).

What is built is what three events need. The first is a failure of the protection of a position: every open position has a stop-loss order resting on the exchange, and if the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user (FR-RSK-7). The second is an entry that is skipped: if the quote balance is below what the plan needs, the entry is skipped and the user is notified (FR-BIN-S3), and so it is when the Futures wallet lacks the margin the plan needs (FR-BIN-F6). The third is an agent that needs attention because the account of its user at Binance conflicts with how it trades: the user is told what to change (FR-BIN-F2). Each is a notification, which is stored for its user and shown on the page of the agent that it is about.

The code is in `src/Ecnanib/Notifications`: `Notification`, `INotifications`, and `StoredNotifications`. The page is `AgentStatus.razor` in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by `NotificationTests` in `tests/Ecnanib.Tests/Notifications`, and by `ProtectiveStopTests`, `SpotEntryTests`, `FuturesEntryTests`, and `FuturesProtectionTests` in `tests/Ecnanib.Tests/DecisionEngine` for when one is raised.

What raises the notification is in [orders.md](orders.md). The email that the same component sends for an account is in [accounts.md](accounts.md).

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
| **It says what happened, in plain words** | The content is the service's own text, in 500 characters at most: what became of a position, with its symbol and its mode. Nothing that a user typed is in it, and no secret. |
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

On Spot the stop-loss is one order of a pair with the take-profit, and a pair that cannot be placed is said as a stop-loss that cannot: the notification is the same. On Futures the two are orders of their own, and the notification says which of them could not be placed. A take-profit that is executed is no failure, and nobody is told.

The page says `ProtectionFailure` as "Order or protection failure", `EntrySkipped` as "Entry skipped", and `AttentionRequired` as "Agent needs attention": `AgentPages.Describe`.

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

- The other events of FR-NTF-1: a position that was opened or closed, an agent that was halted, an agent that needs attention for another cause than its account at Binance or a position without protection, a key that is invalid, a model that is unavailable, a reconciliation mismatch, and a liquidation warning.
- The list of all notifications of a user, and marking one as read.
- Email for critical events, for a user who opted in (FR-NTF-3).
- Showing a notification without a reload of the page (FR-NTF-2).
- Removing notifications after the days of `Retention:NotificationDays` (NFR-DAT-5).
