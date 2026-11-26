# Motion

Changing figures (price, profit and loss, equity) animate from the old value to the new one, and charts animate when they load and when new data arrives (FR-ANI-2). Agent state changes, navigation, dialogs, and list changes use short transitions, and content areas show skeleton placeholders while loading (FR-ANI-3). When the device asks for reduced motion, non-essential animation is replaced by instant changes, and the user can also reduce animation in settings (FR-ANI-4). Animation never blocks input, interface transitions last 150 to 400 ms, continuous animation is used only for states that are live, and nothing flashes more than three times per second (FR-ANI-5). Animations cause no layout shift, and pause when off screen or in a background tab (FR-ANI-6). The team at work, which is the animated pipeline of the agent screen (FR-ANI-1), is in [agent-screen.md](agent-screen.md) and follows the same rules. The public landing page presents the four-provider team with an animated illustration (FR-ANI-7): see "The illustration of the start page".

What moves and for how long is in `src/Ecnanib/wwwroot/app.css`, and nowhere else. When something moves is said by a script of the application, `src/Ecnanib/wwwroot/motion.js`, which `App.razor` names on every page. It is the script of what moves: the application has one more, for the notice before a session ends, which moves nothing and is in [accounts.md](accounts.md). What a page holds is marked by the server: a figure by `PageFigure` and `FigureText.razor` in `src/Ecnanib/WebUi/Animation`, and a state, an item of a list, and a part that moves by an attribute on its element. How much moves for a user is `Motion`, `IMotionChoice`, and the `motion` column of `users`, in `src/Ecnanib/Accounts`, with `PageMotion` and `MotionChoices.razor` in `src/Ecnanib/WebUi/Animation`, and it is chosen on `Appearance.razor`. The illustration of the start page is `TeamIllustration.razor` in `src/Ecnanib/WebUi/Animation`, on `Home.razor` in `src/Ecnanib/WebUi/Pages`, with its texts in `HomePages.cs`. The rules below are enforced by `MotionStyleTests`, `MotionScriptTests`, `MotionMarkupTests`, `MotionPageTests`, and `TeamIllustrationPageTests` in `tests/Ecnanib.Tests/Animation`.

How a page changes without a reload is in [live-updates.md](live-updates.md), the theme that is chosen on the same page is in [themes.md](themes.md), and how a page is kept from carrying a script or a style of its own is in [security.md](security.md). What a page does for a keyboard and a screen reader is in [accessibility.md](accessibility.md).

"Animation" is read as the specification assumes it (A4): motion that shows live state, which is the team at work, changing values, and transitions. Whether that reading is right is an open question of the specification (Q2). The illustration of the start page shows no state that is live: it shows how a team works, once. So it is read as a run of changes, each as short as any other, and as no continuous animation, which is for states that are live (FR-ANI-5).

## What moves

| What | How | For how long |
|---|---|---|
| A figure: a price, a profit or a loss, an equity | The number counts from its old value to its new one, fast at first and slower towards the end. The words around it change at once. | 300 milliseconds. |
| The equity curve | The line unfolds from its first value and fades in, as it is shown and again when a value is recorded. | 400 milliseconds. |
| A page | The content fades in: on a page that is loaded, and on one that the browser goes to without leaving the page that it is. | 200 milliseconds. |
| The state of an agent | What says it fades in when the state is another. | 200 milliseconds. |
| An item that entered a list | It fades in and settles from a little above. An item that left is gone at once. | 200 milliseconds. |
| The dialog that rejoins the server | It rises and fades in over a page that dims, 300 milliseconds after the connection was lost, and closes at once. Its two rings grow and fade one after the other. | 300 milliseconds, and the page under it 200. The rings once in 1.5 seconds, for as long as the server is being rejoined. |
| The placeholder of the content | It fades in. It is gone at once when the page is there. | 200 milliseconds. |
| The team at work | See "What moves" in [agent-screen.md](agent-screen.md). | |
| The team on the start page | See "The illustration of the start page". | |

The figures that count are the price and what the position would make on the screen of an agent, its equity, its net result, and its return there, and on the dashboard the total equity and the profit and loss of the day of each mode, the price and what each open position would make now, and the equity and the day of each agent.

| Rule | Detail |
|---|---|
| **When the device asks for reduced motion, nothing moves** (FR-ANI-4). | Everything in the table is inside `@media (prefers-reduced-motion: no-preference)`. On a device that asks for reduced motion a figure shows its new value at once, a page is there at once, and the rings of the dialog stand still. Nothing is taken back for such a device: nothing is given to it. |
| **When the account reduced it, nothing moves either.** | Every rule inside that block says `:root:not([data-motion="reduced"])` of the page first, each selector by itself. A page whose user chose Reduced says `data-motion="reduced"` on its first element, and is given none of them, whatever the device asks. A device that asks for reduced motion is never overruled: there is no choice that has the pages move regardless. |
| **A change takes between 150 and 400 milliseconds** (FR-ANI-5). | Also the count of a figure, whose time the stylesheet says as `--figure-change`. The dialog waits 300 milliseconds before it rises, as it did before, so a connection that is back at once shows no dialog. The steps of the illustration of the start page wait too, each until the step before it is over, and nothing else waits. |
| **Two things move for as long as a state lasts, each of a state that is live.** | The dot of a role that works, and the rings of the dialog while the server is being rejoined. Each comes and goes once in 1.5 seconds, so nothing flashes. The placeholder does not move by itself. The illustration of the start page is of no state: it is played once, and then stands still. |
| **Only how far something shows and how large it is drawn are changed** (FR-ANI-6). | `opacity` and `transform`, and nothing else: the browser lays nothing out again for them. A figure is as wide as its digits at every step of its count (`font-variant-numeric: tabular-nums`), and counts with as many decimal places as the longer of its two values has. The placeholder is drawn over the content, which keeps its room. |
| **What nobody sees stands still.** | The dot of a role that works is paused while its pipeline is off the screen, and the dot and the rings while the tab is in the background. On a page that its user paused the dot stands still too: the page shows what was so when it was written, and no state of it lasts (see "Pausing" in [live-updates.md](live-updates.md)). A figure that is off the screen or in such a tab is not counted: it shows its new value at once. A figure that is on its way when the tab goes to the background ends at its new value then. The illustration of the start page stands still where it is while its figure is off the screen or its tab is in the background, and goes on from there. |
| **Nothing that moves takes a click or a key.** | What moves is the element itself, never a layer over it, and it can be clicked and reached by keyboard at every step. The placeholder is the one thing that covers anything, and what it covers is the page that is being left. |
| **Nothing relies on what moves** (FR-THM-5). | A value, a state, and an item are said as text, and are the same text on a page where nothing moves. |
| **`MotionStyleTests` holds `app.css` to all of this.** | A rule that moves something for every device or for an account that reduced it, a change that takes a second, a third thing that moves by itself, a wait that is neither the dialog's nor a step's of the illustration, a time that is said apart from its animation, a change of a width or a colour, an animation without its steps, and anything that moves in the other stylesheet each fail it. |

## How a page says what may move

The server marks what a page holds. The marks are attributes and one element, so the content security policy is as it was: no style and no script is written into a page.

| Mark | Where | What it says |
|---|---|---|
| `<data class="figure" value="…">` | Around the number of a figure, inside the sentence that says it. | The number as a script reads it: digits and a decimal point, with a minus for what is less than nothing and no plus. |
| `data-change="…"` | On what says the state of an agent, by the name of the state, on the equity curve, by the time of its latest value, and on the setup checklist of the dashboard: on what says whether a step is done, by that word, and on how many steps are done, by their number. | What the element shows now. |
| `data-key="…"` | On each item of a list that changes: the agents, the open positions, the decisions and the alerts of the dashboard, the rows of the agent list, the notifications, the trades, the decisions, the changes of state and the cycles of an agent, the totals of the dashboard by their asset, and the steps of its setup checklist by their names. | Which item the element is: the id of the row that it shows. |
| `data-moves` | On the pipeline of the team, and on the illustration of the start page. | That something in it moves by itself: for as long as a state lasts, or once. |
| `data-motion="reduced"` | On the first element of the page, beside the theme. | That the account of the user reduced what moves. |

| Rule | Detail |
|---|---|
| **A figure is its number as the page writes it.** | `PageFigure.Of` works the value out from the number that is written, and from nothing else, so the two cannot disagree. The sentence around the number is the one that the page said before: `DashboardPages.Amount`, `Signed`, `PriceOf` and `WouldMake`, `LivePages.Says` and `Unrealized`, and `AgentReportPages.SignedPercent` are each the text of a figure. |
| **A figure that is not known has no number.** | The page says "Not known at the moment" as plain text, and nothing counts to it or from it. |
| **A loss that words say has a value less than nothing.** | "would lose 5.995" is written without a sign, and its value is `-5.995`. A number does not count across a profit and a loss whose words change at once. A figure that writes its sign, `+12.4` or `-5.99`, counts through nothing. |
| **What an element shows is told apart by its key, and never by the element.** | The browser changes a page in its place and gives an element that is there to whatever comes at its place: a row that entered at the top of a list is, to the browser, a new last row and every row above it changed. So an item entered a list when its key is new to the list, and a figure or a state inside an element whose key changed did not change: it is another's. |
| **The number is written where it is.** | On the connection of a page the framework changes the value and the text of a figure in their place and replaces neither, which `MotionMarkupTests` holds. So the script can count in the text that is there, and the connection writes to the same text next time. |

## The script

`motion.js` is one module with no dependency. `App.razor` names it after the script of the framework, as `<script type="module" src="…">`, so it starts once the page is read and holds up nothing. It is a file of this site, which the content security policy lets a browser run: see "The browser" in [security.md](security.md).

| It watches | And does |
|---|---|
| The value of a figure | Counts the number in the text that is there from the old value to the new one, by the frames of the browser, for as long as `--figure-change` says. It ends at the text that the server wrote, to the character. |
| `data-change` | Marks the element `data-changed`, which the stylesheet plays once, and has what moved when the element was first shown move again, which is how the curve unfolds anew. |
| `data-key` | Keeps the keys of each list, and marks an item whose key is new to its list `data-entered`. |
| The browser going to a page without leaving the one that it is | For another address: takes in what the page holds without moving anything, has the page fade in again, and marks the content `aria-busy="true"` when the page is not there after `--loading-after`. For the same address, which is a page that asks for itself again: none of that, and what changed moves. |
| What is on the screen, and whether the tab shows | Marks a part that says `data-moves` with `data-offscreen` while it is off the screen, and the first element of the page with `data-hidden` while the tab is in the background. It does so for any such part, and knows nothing of the illustration. |

| Rule | Detail |
|---|---|
| **A page works without the script.** | Every value then shows at once, as it did before. Nothing in the stylesheet waits for the script to be seen: a page fades in and the curve unfolds by the stylesheet alone, and no element starts out hidden. |
| **The script holds no time and no look.** | It reads its times from the stylesheet, `--figure-change`, `--loading-after`, and `--loading-longest`, and names states as attributes that the stylesheet has rules for. It writes no style and no class. So a page that is given nothing that moves has no time for a figure, and the figure changes at once: the script asks neither the device nor the account what they want. |
| **It asks the server for nothing, and polls nothing** (NFR-ARC-6). | No request, no connection, and no clock that runs again and again. It waits twice, before the placeholder and before it takes the placeholder away. |
| **It writes no markup.** | It writes the text of a figure into the node that is there, attributes, and the sentence that a page brought for a screen reader while the placeholder shows: see "The placeholder". |
| **What a page that the browser went to holds did not change.** | The browser reuses the elements of the page before for the next one, so a figure of the dashboard would count to a figure of an agent. The browser says that it changed the page before the script is told what changed, and the address is another by then: so the script reads what changed at that moment and only takes it in. |
| **The marks that are to last are put back.** | The browser takes every attribute away that the server did not write when it changes a page. `data-offscreen`, `data-hidden`, and `aria-busy` are states that still hold, so the script sets them again. A mark that is played once is taken away when it was played. |
| **What had the focus keeps it on a page that asks for itself again** (NFR-USA-1). | The heading of a page, and what a form was answered with, take the focus by a mark that the framework sets, `tabindex`, which is such an attribute: without it they lose the focus, though they are still there. The script notes what has the focus as the browser begins to change the page, and where that lost it, puts the mark back and the focus with it, without scrolling. A control keeps the focus by itself, and another page gives it where it says. |
| **`MotionScriptTests` holds the script to these, as it is written.** | No test runs it: see "Testing". |

## The placeholder

A page is complete when it arrives: the server reads everything before it answers. So the one wait that a user sees is between asking for a page and its arrival, and that is when the content shows its placeholder.

| Rule | Detail |
|---|---|
| **It shows when a page is on its way for longer than 250 milliseconds.** | `--loading-after`, which holds for every device: it is a time, and nothing that moves. A page that arrives sooner shows none, so a fast page does not flash one. |
| **It is bars where a heading and its lines will be.** | Drawn by the stylesheet over the content, in the colour of the text and faint, in both themes. What the content held keeps its room and is not shown, so nothing shifts when the placeholder comes and when the page does. The bar and the foot stay as they are, and can be used. |
| **The content says that it is busy.** | `aria-busy="true"` on `<main>`, for as long as the placeholder shows. |
| **A screen reader is told that the page is on its way** (NFR-USA-1). | The frame has a line after the content that is not shown, `<p class="visually-hidden" role="status">`, which is empty but for that time. The script writes the sentence into it when the placeholder shows, and takes it out when the page is there. The sentence is `Screens.Loading`, which the page brings in `data-loading`, so the script holds no text. The line stands outside the content, which is not shown for that time. |
| **A page that asks for itself again shows none.** | It is there already, and what changed on it moves. |
| **It goes when the page is there, and when it will not come.** | With the page, and when a page is shown again from the memory of the browser. The framework says nothing of a page that does not come, as when the device is offline: the script hears the request that failed, and gives the content back. It gives it back in any case after 8 seconds, `--loading-longest`, so no page keeps its bars. |
| **The pages are not sent in parts.** | A page that is sent as a placeholder first and filled in afterwards would show its placeholder each time it asks for itself again, which a page that follows does about once a second. |

## The illustration of the start page

Under what the start page says of the team, it presents the team as an illustration (FR-ANI-7): the four roles as the screen of an agent draws them, each with the provider whose model fills it, played once as one cycle.

```
┌─────────────────────┐
│ Market Analyst      │
│ ● A model of GPT    │ ─┐
│ Reads price, …      │  │   ┌─────────────────────┐       ┌─────────────────────┐
└─────────────────────┘  ├──▶│ Strategist          │ ────▶ │ Risk Manager        │
┌─────────────────────┐  │   │ ● A model of Gemini │       │ ● A model of Claude │
│ Sentiment Analyst   │ ─┘   │ Combines both …     │       │ Reviews the plan …  │
│ ● A model of Grok   │      └─────────────────────┘       └─────────────────────┘
│ Reads news and …    │
└─────────────────────┘
The team of a new agent. The Market Analyst and the Sentiment Analyst work at the same time, …
```

| Rule | Detail |
|---|---|
| **It is the team of a new agent.** | Each role has the provider that the configuration file gives it, `Trading:DefaultRoles` (FR-TEAM-2, FR-TEAM-3), by the name that the catalog has for the provider: `ICatalogModels.ProviderNamesAsync`, which reads the rows of the providers and nothing else. The name and the job of a role are those of the form of an agent, `AgentPages.Describe`. A mapping that the file changes is the one that the page shows, also one that gives a provider two roles. |
| **It is the pipeline of the agent screen, as it is once a cycle has gone through it.** | The same three stages, cards, markers, and lines, by the same rules of the stylesheet: see "The team at work" in [agent-screen.md](agent-screen.md). Every role is done, and the work was handed to each stage. `TeamIllustration.razor` writes it as a figure, which no other page has. |
| **A provider is named as text, to say which it is, and by nothing else** (NFR-LEG-4). | "A model of GPT". The page has no logo and no picture, and a provider has no colour of its own. It names no company behind a provider, and says nothing that would have a provider stand behind the service. An official logo is used only where the terms of its brand allow it: none is used. |
| **A caption says what the picture shows.** | Whose team it is, and the order that the roles work in, in words. Nothing relies on what moves, or on a colour (FR-THM-5). |
| **The cards and their texts never move, and are never hidden.** | What moves is drawn for the eye alone and hidden from a screen reader: the marker of a role, the second outline of its card, which is an empty element at the start of the card, and the line into a stage. So a page without the script, and a browser that lacks something of it, show the whole picture. |
| **A visitor and a user see the same.** | The start page is one page. For a user who reduced what moves it stands still, as every page does. It stores nothing and follows nothing. |

### What moves

| Step | How | For how long | It waits |
|---|---|---|---|
| The two analysts take up their work, at the same time | A dot shows in the ring of each, and the second outline of its card. | 200 milliseconds. | 400 milliseconds. |
| They are done | The disc grows from under the dot, and the second outline goes. | 200 milliseconds. | 1.2 seconds. |
| The work is handed to the Strategist | The line into its stage is drawn through. | 300 milliseconds. | 1.4 seconds. |
| The Strategist takes up its work, and is done | As the analysts. | 200 milliseconds each. | 1.7 and 2.5 seconds. |
| The work is handed to the Risk Manager | The line into its stage is drawn through. | 300 milliseconds. | 2.7 seconds. |
| The Risk Manager takes up its work, and is done | As the analysts. | 200 milliseconds each. | 3 and 3.8 seconds. |

| Rule | Detail |
|---|---|
| **It is played once, as one cycle, and then stands still** (FR-ANI-5). | It is of no state that is live, so nothing of it goes on by itself. After 4 seconds it is the picture that the page was sent. It is played again when the page is shown again, and not when the page is asked for where it is. |
| **Each step is a change of 150 to 400 milliseconds, and waits until the step before it is over.** | The first waits until the page has faded in. The waits are what make it one run: they are the one thing of it that a change of the interface does not have. |
| **A step says what a part comes from, and never what it goes to.** | And holds that until its turn. So the run ends as the rules outside it say, which is the picture that stands still, by construction: no rule says the end twice. |
| **When the device asks for reduced motion, or the account reduced it, the picture stands still** (FR-ANI-4). | The run is inside `@media (prefers-reduced-motion: no-preference)`, each rule behind `:root:not([data-motion="reduced"])`, as everything that moves is. Such a page is given the whole picture at once. |
| **It stands still while nobody sees it** (FR-ANI-6). | The figure says `data-moves`, so the script marks it `data-offscreen` while it is off the screen, and the page `data-hidden` in a tab in the background. The figure says so to its parts by a property, `--team-run`, and each step takes whether it plays from that, after its animation. An animation says "running" with everything else that it says, so a rule that only named the figure would lose to the rule of a step, and nothing would say so. |
| **So a figure below the first screen is played when it is scrolled to.** | It is held before its first step. Where the script arrives later than that step waits, the first steps are played unseen, and without the script all of it is: the picture is whole in either case. |
| **Only how far something shows and how large it is drawn are changed** (FR-ANI-6). | The second outline comes once and goes once, by how far its element shows and how far what is drawn in it shows. Nothing around a card moves. |
| **Nothing of it takes a click or a key.** | The figure holds no link and no control. |
| **`MotionStyleTests` holds `app.css` to these.** | The steps by their rules, in the order that the team works in, each played once and as its first step until its turn, that only what is drawn for the eye moves, that a step waits for the one before it and the whole is over in 5 seconds, that each step takes whether it plays from its figure, and that the picture that stands still is outside. |

## Choosing how much moves

The page of the appearance among the settings, `/account/appearance`, has a second form under the theme: "Animation", with System and Reduced, each with what it means below it. The one that holds now is ticked.

| | |
|---|---|
| System | The pages move as they change, unless the device asks for reduced motion. It is what a new account has. |
| Reduced | A change shows at once and nothing moves by itself, whatever the device asks for. |

| Rule | Detail |
|---|---|
| **It is stored with the account, beside the theme.** | The `motion` column of `users`, by the name of the choice. It holds on every device that the user signs in on, from the next page on. A new account has `System`, and so has every account that there was before: the migration `AddMotion` gives them that. |
| **Nothing is stored for a visitor.** | The pages of a visitor move as their device asks, and the page of the appearance of a visitor offers the theme alone. Signed out, a device is one of a visitor again. |
| **The server writes it into every page, ahead of the stylesheets.** | `PageMotion.Of` reads it from the user of the request, and `App.razor` writes `data-motion="reduced"` beside `data-theme`, and no attribute for System. So nothing of a page moves while a stylesheet or the script is on its way. The server is never told what the device asks: that is for the stylesheet to see. |
| **A request is told it as it is signed in.** | It is read in the query that looks up the session, with the theme, and is a claim of the user of the request: `SessionAuthentication.MotionClaimType`, which `SignedInUser.MotionOf` reads. |
| **Each of the two forms saves its own choice.** | The theme and what moves are chosen each by itself, and a form does not read the field of the other. |
| **The form is as the one of the theme.** | Posted to its page with the token of the page, naming no user, asking for no proof of identity, and in no event of the audit trail. A form that names no choice saves nothing, and the page says "Choose System or Reduced." |
| **It is a preference, and personal data of an allowed kind** (NFR-SEC-9). | One of two names. It is in the export of the user's data, under `account`, and goes with the account when it is deleted. |

## Browsers

The stylesheet and the script keep to what the current and the previous major versions of Chrome, Edge, Firefox, and Safari have. A browser that lacks something of it shows a change at once: every rule of what moves is one that a page reads well without.

## Testing

| Test class | What it holds |
|---|---|
| `MotionStyleTests` | What moves, in `app.css`: nothing for a device that asks for reduced motion, nothing for an account that reduced it, nothing in the other stylesheet, a change of 150 to 400 milliseconds, what may wait, the two things that move by themselves and that each stands still in a tab that nobody sees, only `opacity` and `transform`, the steps of every animation, that the times of an animation are said where it is given, the run of the illustration of the start page, and the times that the script reads. |
| `MotionScriptTests` | The script as it is written and served: every page names it once, after its stylesheets; it asks the server for nothing and polls nothing; it writes no markup and no style; it holds no time, and reads the ones that the stylesheet says; and what it marks is what the stylesheet reads. |
| `MotionMarkupTests` | What the server writes for a figure: its value from its number, the sentences as they were, no number for what is not known, a loss that words say, the element as a page writes it, and that a figure which changes on the connection of a page is changed in its place. |
| `TeamIllustrationPageTests` | The illustration as the start page is sent: the four roles in their order and their stages, each with the provider of the configuration file by the name of the catalog, a file and a name that change, a provider as text in one sentence and no picture on the page or in a stylesheet, that the figure says that it moves and carries no style, no script, and nothing to click, and that a user, and one who reduced what moves, is sent the same picture. |
| `MotionPageTests` | The choice: its two names and the default, an account from before it, a choice that every page says ahead of its stylesheets, on every device and on none once signed out, the export, each form by itself, another user, a form that names no choice, and a form without its token. |
| `DashboardPageTests`, `AgentScreenPageTests`, `LivePageTests` | The marks as the pages write them: the figures of the dashboard with their elements, the keys of its totals, positions, agents, and decisions, the state of an agent by its name, the time of the latest value on the curve, and the price and what a position would make on the screen of an agent. |
| `LayoutPageTests`, `DataExportTests` | The forms of the page of the appearance, and the choice in the export. |

No test of these drives a browser, and none runs the script of what moves: `AccessibilityBrowserTests`, which a run starts when it is told to, opens every screen with what moves reduced, so what it measures stands still (see [accessibility.md](accessibility.md)). A figure that is below the first screen of a page is not counted until it is scrolled to, which is as it should be: look at one that shows. By hand:

1. `docker compose up -d`, and run the application. Sign in, create an agent in Demo, start it, and open the dashboard and the screen of the agent in two windows.
2. Add a row at the database, such as `INSERT INTO equity_snapshots (agent_id, user_id, mode, at, asset, capital, equity) VALUES (...)`: within two seconds the curve unfolds again, with no reload. With an open position whose price is followed, the price counts as it is read, and so do what the position would make and the equity.
3. Go from the dashboard to the agent and back by the links: the content fades in, and no figure counts from a number of the other page. A new notification fades in at the top of its list, and no other item does.
4. Slow the network down in the developer tools and follow a link: after a moment the content shows its bars, nothing shifts, and the page comes. Go offline and follow a link: the content is back at once. Follow one link and then another before the first page is there: the second page shows, with no bars left.
5. Ask the browser to emulate reduced motion, in the rendering panel of its developer tools: nothing moves, and every value changes at once. Turn that off, choose Reduced on the page of the appearance, and the same holds. Choose System again, and the pages move.
6. Hide the tab while a figure counts, and come back: it says its new value. Scroll the pipeline of a running cycle off the screen: the dot of a role that works is paused, as the animations panel of the developer tools shows.
7. Open `/`, signed out: under what the page says of the team, the two analysts take up their work, are done, the line into the Strategist is drawn, and so on to the Risk Manager, once. Then nothing moves. Load it again at the width and the height of a small phone, where the figure is below the first screen: it is played when it is scrolled to, and stands still when it is scrolled away or its tab is hidden. With reduced motion, of the device or of the account, the whole picture is there at once. Follow a link to `/` from another page: it is played once.
8. Stop the application while a page that follows is open: the page dims, the dialog rises with its rings, and with Reduced it is there at once with rings that stand still.
9. Do the same in Chrome or Edge, in Firefox, and in Safari, on a light and on a dark screen, and at the width of a phone. The console of the browser shows no report of the content security policy.

## Not built yet

- The illustration of the start page played again on request. It is played once each time that the page is shown.
- A logo of a provider, on the start page or anywhere else. A provider is its name as text, until the terms of its brand are read and allow more (NFR-LEG-4).
- An item that leaves a list moving as it goes. It is gone at once.
- A placeholder on a page that is loaded whole: after a form, and on the first page of a visit. The browser shows the page before until the next one is there.
- A placeholder in the shape of the page that is on its way. It is the same bars for every page.
- A choice for a visitor, saved on the device as the theme is. A visitor has what their device asks.
- A check that what moves holds 60 frames per second on a mid-range device (FR-ANI-6). What moves is `opacity` and `transform`, and the text of one number at a time.
