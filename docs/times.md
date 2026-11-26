# Times

Times are shown in the user's local time zone, with UTC available (NFR-USA-3). The device of a user says its time zone when the user signs in, and the server keeps it on the device. The account says whether its pages are in the time zone of the device, which a new account has, or in UTC. The server writes every time of a page in the time zone of that page, and each time says how far that is from UTC: `15 November 2026, 10:20 UTC+1`. Timestamps are stored in UTC, as before (NFR-DAT-8).

The choice of an account is `TimeDisplay`, `ITimeDisplayChoice`, and the `time_display` column of `users`, in `src/Ecnanib/Accounts`. What writes a time is `PageTimes` in `src/Ecnanib/WebUi/Pages`. What decides the time zone of a page, and the cookie of a device, are `PageTimeZone` and `DeviceTimeZone` in `src/Ecnanib/WebUi/TimeZones`, with the field of a form in `DeviceTimeZoneField.razor` and the fields of the choice in `TimeChoices.razor` there. The script is `src/Ecnanib/wwwroot/times.js`. The choice is on `Appearance.razor` in `src/Ecnanib/WebUi/Pages/Account`, with its texts in `AppearancePages.cs`. The rules below are enforced by `PageTimesTests`, `TimePageTests`, `TimeScriptTests`, and `TimeZoneCoverageTests` in `tests/Ecnanib.Tests/Times`.

How a page is kept from carrying a script of its own, and the cookies of the application, are in [security.md](security.md). The theme and how much moves, which are chosen on the same page, are in [themes.md](themes.md) and [motion.md](motion.md). Where the words of a page are is in [texts.md](texts.md), and what is stored about a person in [accounts.md](accounts.md).

## How a page gets its time zone

**The server writes every time.** A script says which time zone the device is in, once, into a form. What a time says is the server's alone, so a page says a time the same way with a script and without.

| Step | What happens |
|---|---|
| 1. The device says its time zone. | The form of the sign-in has a hidden field, `TimeZone`. The script of the times writes into it what the browser says: the name of a time zone as the time zone database has it, such as `Europe/Berlin`. |
| 2. The server keeps it on the device. | When the password was right, the page of the sign-in keeps what the form said in the cookie `__Host-ecnanib-time-zone`: `DeviceTimeZone.Keep`. For an account with two-factor authentication that is the answer to the password, because the page that takes the code has no such field. |
| 3. The server decides the time zone of each page. | `PageTimeZone.Of`: UTC for a user whose account says UTC. Otherwise the time zone that the device has said, and UTC for a device that has said none. |
| 4. Every time is written in it. | `App.razor` hands the times of the request, a `PageTimes`, down to every page and every part of one. A page asks them for each time that it writes: `Times.At(agent.StateChangedAt)`. |

| Rule | Detail |
|---|---|
| **There is no way to write a time without the times of the page.** | `PageTimes` has no method that writes a moment for everyone. A page takes `[CascadingParameter] PageTimes Times`, and a text class that puts a time into a sentence is handed them: `AgentPages.HaltEnds(until, times)`. So no page writes a time in a time zone of its own, and the compiler holds that. |
| **Every screen is held to it.** | `TimeZoneCoverageTests` reaches every state of every screen, as the accessibility of the pages is held on them, by people whose devices say `Asia/Kolkata`. Every time that a page shows there says `UTC+5:30`. A part that was handed no times would write UTC, and be named. |
| **A device that has said no time zone reads UTC.** | A browser that runs no script, and an account that was signed in before this was built. The times say `UTC`, as they always did, and the page of the appearance says that the device has said none. |
| **No code asks for the time zone of the machine.** | The machine that the application runs on is in the time zone of no user. `ComponentRules.cs` forbids `TimeZoneInfo.Local`, `DateTime.Now`, `DateTimeOffset.Now`, and `ToLocalTime`, and `DependencyRuleTests` holds every component to it. |
| **The content security policy is as it was.** | The script is a file of the site, and no script and no style is written into a page for a time. `BrowserDefenseTests` opens every page as before. |

## How a time is written

| Form | For | In UTC | In `Asia/Kolkata` |
|---|---|---|---|
| `At` | A moment as a person reads it, to the minute. | `15 November 2026, 09:20 UTC` | `15 November 2026, 14:50 UTC+5:30` |
| `AtSecond` | What happens within a minute, as the steps of a cycle do. | `15 November 2026, 09:20:07 UTC` | `15 November 2026, 14:50:07 UTC+5:30` |
| `TimeOfDay` | What happens today and soon, as the end of a session does. | `09:20 UTC` | `14:50 UTC+5:30` |
| `Day` | The day of a moment, as the calendar of the time zone has it. | `15 November 2026` | `15 November 2026` |

| Rule | Detail |
|---|---|
| **A time says how far its time zone is from UTC.** | `UTC+1`, `UTC-5`, `UTC+5:30`. So a time is never taken for one of another time zone, and UTC is one sum away. A name such as "CET" is not written: a name is known where it is used, and several time zones share one. |
| **It is the distance at the moment itself,** and not that of today. | A moment in summer says the summer time of its time zone, also when it is read in winter: `PageTimes.OffsetAt` asks the time zone database for the moment. |
| **A time zone that is at UTC at a moment says `UTC`.** | London in winter reads as a page in UTC does, and in summer `UTC+1`. |
| **A page in UTC writes a time as it always did.** | Word for word. The tests of the pages that say a time sign in on a device that says no time zone, and hold what they held before. |
| **A page writes the sign as an entity.** | `UTC&#x2B;1` in the markup, as a page writes every sign that markup could be made of. A browser shows `UTC+1`, and a test reads a page as a browser shows it. |
| **The words are English, for everyone.** | The name of a month and the order of a date are those of the pages, whatever the device is set to. See "Adding a language" in [texts.md](texts.md). |

## What stays UTC

Not everything that looks like a time is a moment that a clock on a wall could show.

| What | Where | Why |
|---|---|---|
| **A day of the AI usage** | The rows of the page of the AI usage, and "from … until now" above them: `PageTimes.UtcDay`. | The usage is added up by UTC day, and the page says "A day is a UTC day." A row that was moved into a time zone would be the sum of another day. See "AI usage" in [team.md](team.md). |
| **A day that a provider says of a model** | Released, deprecated, and retires, on the page of the catalog: `CatalogPages.Day`. | It is a date, stored as the start of a UTC day. West of UTC it would be written as the day before. When a model was last listed is a moment, and is the day of the time zone of the page. See [catalog.md](catalog.md). |
| **"The UTC day", in words** | Where a page says what a daily loss limit counts over, and what "today" is on the dashboard. | The loss of an agent is counted by UTC day, and the candles close on UTC boundaries (FR-CYC-1, FR-RSK-8). The moment that a halt ends at is written in the time zone of the page beside it. |
| **An email** | `NotificationEmails` writes when each notification was raised in UTC, and says so. | An email is written for no request and read on any device. Notifications may not use the Web UI: see [architecture.md](architecture.md). |
| **A file to keep** | The CSV of the trades and of the decisions, the JSON of the data of an account, and the dates in their names. | A file is the same wherever it is opened, and a program reads it. See [export.md](export.md). |
| **What a model is told, and the log** | The data package of a cycle, and every line of the log. | Neither is a page. See [cycles.md](cycles.md) and [logging.md](logging.md). |

## Choosing

The page of the appearance among the settings, `/account/appearance`, has a third form, under the theme and how much moves. It offers two choices, each with what it means below it, and the one that holds now is ticked.

| Choice | Stored as | What it means |
|---|---|---|
| This device | `Device`, which a new account has | Times are written in the time zone of the device that the user signs in on. A user who signs in at home and on a journey reads the time of where they are. |
| UTC | `Utc` | Times are written in UTC, whatever the device says: the time that Binance and the candles go by. |

| Rule | Detail |
|---|---|
| **The account says which of the two, and not which time zone.** | Which time zone a device is in is the device's to say, and is kept on the device. So the account stores one of two names, which cannot say where a person is. |
| **A new user has the device,** and so has every account that there was before: the migration `AddTimeDisplay` gives them that. | UTC is one choice away, on every device. |
| **The page says what the device has said.** | "This device has said that its time zone is Europe/Berlin, which is UTC+1 now.", whatever the account says. For a device that has said none it says so, and how the device comes to say one. |
| **Saving "This device" keeps what the device says now.** | The form carries the hidden field of the sign-in. So a device that has moved to another time zone, or that was signed in on before this was built, is not signed in on again for it. Saving UTC leaves what the device said as it is. |
| **The form is posted to its page, with the token of the page,** and names no user. | As the forms of the theme and of the motion: see [themes.md](themes.md). The choice is of whoever is signed in: `ITimeDisplayChoice.SetAsync` takes the user from the session (NFR-SEC-3). |
| **A form that names no choice saves nothing.** | The page comes back as it was, and says "Choose This device or UTC." |
| **The choice asks for no proof of identity, and is in no event of the audit trail.** | It exposes no key and moves no money. |
| **A request is told the choice of its user as it is signed in.** | It is read with the session, in the same query, and is a claim of the user of the request: `SessionAuthentication.TimeDisplayClaimType`, which `SignedInUser.TimeDisplayOf` reads. See "How a request is signed in" in [accounts.md](accounts.md). |
| **The choice is a preference, and personal data of an allowed kind (NFR-SEC-9).** | It is in the export of the user's data, under `account`, and goes with the account when it is deleted. The page of the account names it. |

## The device

### The cookie

| | |
|---|---|
| Name | `__Host-ecnanib-time-zone` |
| Value | The name of a time zone, as the device said it: `Europe/Berlin`. |
| Attributes | `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, and `Max-Age` of one year, which starts anew with each sign-in on the device. |
| Who reads it | The server alone, to write the times of a page. So it is hidden from scripts, as every cookie is. |
| What it says | Which time zone a device is in, and nothing of who its user is. Nothing is stored on the server for it. |

| Rule | Detail |
|---|---|
| **It is of the device, and not of whoever is signed in.** | Signing out does not take it back, and neither does deleting an account. A browser that signs in next without the script reads the same time zone still. |
| **Only the device of whoever knew the password is kept a time zone for.** | A sign-in that failed keeps nothing. |
| **What names no time zone is not kept, and nothing is written for it.** | Not even the taking back of a cookie: what the device said before stays as it is. A sign-in from a browser without the script sets the same cookies as before. |
| **What a device says is checked before it is looked up, and again when it is read.** | A time zone is found by its name among the files of the time zone database. So only what is written as such a name is looked up at all: letters, digits, `_`, `+`, and `-`, in three parts at the most with `/` between them, and 64 characters at the most. A dot, a blank, or a slash at the start is no name. `PageTimesTests` and `TimePageTests` hold it to that with what a browser should never send. |
| **What a cookie says is written into no page as it is.** | A page writes the name only after it named a time zone that the machine knows. |

### The script

`times.js` is the third script of the application, beside `motion.js` and `session.js`: see [security.md](security.md).

| Rule | Detail |
|---|---|
| **It writes the value of a field, and nothing else.** | Of every `input[data-time-zone]` of the page: what `Intl.DateTimeFormat().resolvedOptions().timeZone` says. When the page is there, when the browser went to a page without leaving this one, and as a form is sent. |
| **It asks the server nothing, and keeps nothing.** | No request, no cookie, no storage. The form that a person sends carries what it wrote. |
| **It writes no time, and holds no text.** | No moment is read and none is written, so no page says a time that the server did not write. |
| **A page works without it.** | The field stays empty, nothing is kept, and the times are in UTC. |

## The parts that follow

A part of a page that is written over the connection of the page has no request to ask, and nothing that a page hands down reaches it: see [live-updates.md](live-updates.md). So the page says its time zone to its parts.

| Rule | Detail |
|---|---|
| **The time zone travels with the page to its connection, as text.** | `LiveView.TimeZone` holds its name, or nothing for UTC. `LiveParts` reads it again with `PageTimes.In`, and hands the times to the steps of a cycle and to the position. |
| **A part says a time the same as it is written with the page and over the connection.** | Both read the same name. So nothing changes on a page when its connection is made. |
| **The notice before a session ends is answered in the time zone of its request.** | `/session/left` and `/session/stay` write the sentence that the dialog shows with `PageTimes.Of(request)`. |

## The machine

The time zones are those of the machine that the application runs on.

- **The machine needs the time zone database.** On Linux that is the package `tzdata`. An image without it, such as a minimal Alpine image, knows no time zone but UTC: every device is then answered in UTC, and nothing fails.
- **A browser may say an older name of a time zone.** Chromium says `Asia/Calcutta` for a device in India, where the time zone database says `Asia/Kolkata`. A machine may have the older names or not: some systems keep them in a package of their own. So a name that the machine does not know is looked up once more by its newer name, from the list of `DeviceTimeZone.Renamed`: the nineteen older names that browsers go by. A name that is on neither is no time zone, and that device reads UTC.
- **The name is not shown unless it was found.** So what the page of the appearance says is true of the machine that answered.

## Testing

| Test class | What it holds |
|---|---|
| `PageTimesTests` | How a time is written, with no application. That a page in UTC writes each form as it always did. A time in six time zones in winter and in summer, with whole hours, half hours, and three quarters, ahead of UTC and behind it. That every form says the same moment, that a time zone at UTC says `UTC`, and that the day of a moment is the day of the time zone. That a UTC day is not moved. That what names no time zone is UTC: nothing, a name that there is not, a path, markup, a line break, and a name that is too long. That an older name which a browser still says is the time zone of its newer name. That the name of a time zone is read again as the same time zone. |
| `TimePageTests` | The two choices and their names. That a device says its time zone with the sign-in, with the cookie and its attributes, and that the times of its user say how far it is from UTC. That a device which says none reads UTC, and that the page says so. That an account from before has the device. That what names no time zone is not kept, from the form and from a cookie, and that a wrong password keeps nothing. That two-factor authentication keeps it with the answer to the password. That the day of a moment is the day of the time zone. That UTC holds on every device, leaves the cookie, and that coming back keeps what the device says now. That the choice of one user is not that of another, that a form which names no choice saves nothing, and that one without its token is refused. That signing out leaves the cookie. That a page of an agent says the same moment in the time zone of each of two devices. |
| `TimeScriptTests` | That every page names the script once, after the script of the framework. That it asks the server nothing and does nothing by a clock, writes the value of a field and nothing else, keeps nothing, holds no address and no sentence, and writes no time. |
| `TimeZoneCoverageTests` | Every state of every screen, for devices that say `Asia/Kolkata`: every time that a page shows is in that time zone. |
| `LiveFeedTests` | That the time zone of a page travels to its connection as text, and is read again as the same. |
| `LayoutPageTests`, `AccessibilityMarkupTests` | That the page of the appearance has its third form, and that its states keep to the rules of the markup. |
| `DataExportTests`, `TransportTests` | That the choice is in the export, under `account`, and that a sign-in sets the cookies that it set. |
| `DependencyRuleTests` | That no code asks for the time zone or the local time of the machine. |

No test of these drives a browser, so none runs the script. `AccessibilityBrowserTests` does, when a run is told to: see [accessibility.md](accessibility.md). Its browsers are set to UTC, so that a run reads the same on every machine, but for one test, which signs in on a device in `Asia/Kolkata`: the script writes the time zone into the form, the server keeps it in a cookie that the page cannot read, and the page of the appearance says it, also after UTC was chosen and the device again. By hand:

1. `docker compose up -d`, and run the application.
2. Sign in. The dashboard, the screen of an agent, and the notifications say their times in the time zone of the device, each with how far it is from UTC. The tools of the browser show the cookie `__Host-ecnanib-time-zone`, marked `HttpOnly`.
3. Open "Appearance" among the settings. The page says which time zone the device has said. Choose UTC, and save: every time says `UTC`. Choose "This device" again: the times are local again.
4. Set the device to another time zone, and save "This device" once more: the page says the new one, without signing in again.
5. Open the screen of an agent whose cycle runs. The times of its steps do not change when the connection of the page is made.
6. Turn scripts off in the browser, take the cookie away, and sign in: every time says `UTC`, and nothing else differs.

## Not built yet

- A time zone that the user names, other than the one of the device and UTC: a list to choose from, stored with the account.
- The local time in an email, which would go by such a named time zone, and in a file to keep.
- A time as an element that says its moment to a browser, `<time datetime>`: see "Not built yet" in [accessibility.md](accessibility.md).
- The days of the AI usage by the day of the user: the usage is added up by UTC day.
- A device that changes its time zone while its user stays signed in reads the old one until "This device" is saved again, or the user signs in again.
