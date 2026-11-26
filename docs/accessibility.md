# Accessibility

The UI meets WCAG 2.1 AA, is fully operable by keyboard, and labels controls for screen readers (NFR-USA-1). Each screen passes an accessibility check and a keyboard walk-through before release.

This is what every page does for that, and how every screen is held to it. A page is rendered on the server and is made of what a browser knows by itself: headings, links, buttons, fields with their labels, tables, and two dialogs. So most of what a keyboard and a screen reader need is there because nothing was put in its way, and the rules below say what was added, and what is checked so that it stays.

What takes the focus when a page opens is `Screens.FocusOnLoad` in `src/Ecnanib/WebUi/Layout`, with `Routes.razor`. What a field says of itself is `PageFields` in `src/Ecnanib/WebUi/Pages`. The notice before a session ends is `SessionNotice.razor` in `src/Ecnanib/WebUi/Layout` with `src/Ecnanib/wwwroot/session.js`, and what pauses a page that follows is `LivePause.razor` in `src/Ecnanib/WebUi/Live`. The look is in `src/Ecnanib/wwwroot/app.css`. The rules below are enforced by the tests in `tests/Ecnanib.Tests/Accessibility`, whose list of screens and of rules is `AccessibilityRules.cs` there, and by `SessionNoticeTests`, `SessionScriptTests`, and `LivePageTests`.

The frame of every page is in [layout.md](layout.md), its colours in [themes.md](themes.md), what moves in [motion.md](motion.md), how a page follows what changes and is paused in [live-updates.md](live-updates.md), and the session of a user in [accounts.md](accounts.md).

## What every page has

| Rule | Detail |
|---|---|
| **It says its language and has a title.** | `<html lang="en">`, and a `<PageTitle>` that says what the page is, which is the text of its heading. |
| **It has one heading, and its headings go down a level at a time.** | One `<h1>`, in the content, and under it `<h2>` and so on, with none left out. So a screen reader lists the parts of a page as they are. |
| **Its parts are told apart.** | The bar is a `<header>`, the content a `<main>`, and the foot a `<footer>`. Each navigation has a name: "Main", "Settings", "Legal", "This device", and the pages of a list. A table that scrolls is a part with the name of its heading. No two parts of one kind share a name on a page: where a page has the same heading twice, as the dashboard has "Recent decisions" under Demo and under Live, and the screen of an agent a check under each decision, the part is named by the heading above it and its own. |
| **Its first link leads past the bar.** | "Skip to the content": see "The frame" in [layout.md](layout.md). |
| **Every control is one that the browser knows.** | A link, a button, a field, a choice, and the element that opens the menu. Nothing else takes a click or a key, so everything that a pointer reaches a keyboard reaches too, and a screen reader says what each is. |
| **Every control has a name.** | A field has a `<label>`, choices of one question stand in a `<fieldset>` with its `<legend>`, and a link and a button say in their text what they do, with what they do it to: "Delete the Claude key", "State of Swing". A name is never only a `title`, which a keyboard does not show. |
| **Every table says what it is, and names its rows and columns.** | A caption or the heading above it, `<th scope="col">` for each column, and `<th scope="row">` for the cell that names a row. |
| **A picture says what it shows, or is hidden.** | The equity curve and the QR code are `role="img"` with a name, and the curve is said in words beside it. A mark that only decorates is `aria-hidden`. The pages have no image of text. |
| **Nothing is said by a colour alone, or by what moves.** | A state, a mode, an outcome, a profit, and a loss are words and signs: see [themes.md](themes.md) and [motion.md](motion.md). |
| **Text reads on its page, and a line that tells a control apart shows.** | With a contrast of 4.5 to 1 for text and 3 to 1 for such a line, in both themes: see "Colours" in [themes.md](themes.md). A page says the colours of the browser for itself and its text, `Canvas` and `CanvasText`, so a tool that measures contrast reads them off the page. |
| **A page can be enlarged, and fits a narrow screen.** | Sizes are in `rem`, nothing keeps a browser from enlarging the page, and at the width of a phone, which is also a desk at twice the size, a page does not scroll sideways: a table that is wider scrolls by itself. See "Narrow and wide" in [layout.md](layout.md). |
| **A field says what it is for.** | `autocomplete` on the address, the password, and the code of a user, so a browser and a password manager fill them. A key of a provider is not of the user, and says `off`. |

## The keyboard

| Rule | Detail |
|---|---|
| **What has the focus shows.** | A line around it, two pixels wide, in the colour of the text of its page: `:focus-visible` in `app.css`. It is the same on every control, on a light page, on a dark one, and in a part that is light in both. |
| **Two things take the focus that are no controls, and show no line.** | The heading of a page, when the page opens, and the content, when the link past the bar is followed. `AccessibilityRules.WithoutOutline` lists them, and a rule that takes the line away from anything else fails `AccessibilityStyleTests`. |
| **The order of the keyboard is the order of the page.** | No element is given a place of its own in it. |
| **A table that scrolls is a stop of the keyboard.** | `tabindex="0"` on the part around it, which has a name: the arrow keys scroll it. It is the one kind of stop that is no control. |
| **The menu of a narrow screen opens and closes by keyboard.** | It is an element that the browser opens and closes: see "Narrow and wide" in [layout.md](layout.md). |
| **A dialog keeps the focus while it is open, and gives it back.** | Both dialogs are `<dialog>` elements that the browser opens over the page: the focus moves into them, stays out of the page behind them, and returns to where it was. Each has a name. |
| **A page that asks for itself again keeps the focus where it was.** | A control keeps it by itself. The heading of a page, and what a form was answered with, have it by a mark of the framework, which the browser takes away when it changes the page in its place: the script of the pages puts the mark back, and the focus with it. See "The script" in [motion.md](motion.md). |
| **Pausing a page, and having it follow again, keeps the focus on the link that did it.** | The part of a page that is written over its connection stands in an element of its own, so the browser changes that element and nothing around it: see "Pausing" in [live-updates.md](live-updates.md). |
| **Nothing needs a pointer.** | No part shows only while a pointer is over it, and nothing is dragged. |

### When a page opens

`FocusOnNavigate` in `Routes.razor` gives the focus to what `Screens.FocusOnLoad` selects, once the page is read.

| The page | What takes the focus |
|---|---|
| Any page that is opened | Its `<h1>`. A screen reader says what the page is, and the next stop of the keyboard is in the content. |
| A page that answers a form with something above the form | That: `.form-error`, what could not be done, or `.form-done`, what was done. It is read out first. |
| A page that answers a form by naming fields as wrong, and says nothing above it | The first of those fields, which asks for the focus itself with `autofocus`, and is read out with what says why. The browser brings it into view. |
| The editor of an agent that asks once more before it saves for Live | The question, at the foot of the form, which asks for the focus itself. |
| The editor of an agent after a button that shows something and stores nothing | The choice that the button filled anew, so the keyboard is where the form was left. |
| Another page of a list | Its `<h1>`: the links to the pages of a list open a page as any link does. |

| Rule | Detail |
|---|---|
| **At most one element of a page asks for the focus.** | And none inside a dialog: the framework gives the focus to nothing when an element asks for it, so a dialog that asked would take the heading's turn on every page. A dialog gives the focus to its first button by itself when it opens. |
| **A field asks for the focus only where nothing stands above the form.** | What stands above the form takes it otherwise. So a page never has two answers to where the focus is. |

## After a form

Every form is sent to the server and answered by a page. What the page says of it is where the focus is, in words, and at the field that it is about.

| Rule | Detail |
|---|---|
| **What could not be done is said above the form.** | `<p class="form-error" role="alert">`. It takes the focus when the page opens. |
| **What was done is said above the form too.** | `<p class="form-done" role="status">`, for a form that is answered in place: a key that was checked, a position that was closed, two-factor authentication that was turned on. A form that leads to another page is answered by that page. |
| **What a form asks once more is a warning.** | `<p class="form-warning" role="alert">`, which takes the focus itself. |
| **A field that was refused is marked, and names what says why.** | `aria-invalid="true"`, and `aria-describedby` with the id of what says why first and the id of its hint after it: `PageFields.Invalid`, `PageFields.ErrorOf`, and `PageFields.DescribedBy`. What says why is `<p id="{field}-error" class="field-error">` under the field. So a screen reader says it with the field, each time the field has the focus. |
| **What a page says under a choice is named by the choice.** | In the editor of an agent, the notes under the provider and the model of a role, and what the form recommends for the role. |
| **A notice that stands is no answer.** | `<p role="status">` and `<p role="note">` without those classes say how things are: that an address is not verified, that an agent is in Live. They do not take the focus. |
| **The browser checks what it can before a form is sent.** | `required`, `type="email"`, and `minlength`, and the server checks all of it again. |
| **A link that opens beside a form says so.** | The legal documents on the form that registers and on the page that switches an agent to Live open in a new tab, so that what was typed stays, and say "(opens in a new tab)" in their own text. |
| **Whatever changes money or cannot be undone is asked about on a page of its own.** | See [agents.md](agents.md) and [accounts.md](accounts.md). |

## What changes by itself

| What | How a user is told, and how they stop it |
|---|---|
| The team at work, on the screen of an agent | Each cycle is a part that a screen reader reads out as it changes, politely, and each role is read out whole: see "The team at work" in [agent-screen.md](agent-screen.md). |
| A page that follows what changes | It says so under its heading, with "Pause live updates". A paused page makes no connection and changes no more by itself: see "Pausing" in [live-updates.md](live-updates.md) (WCAG 2.1, 2.2.2). |
| What moves | Nothing moves for a device that asks for reduced motion, nor for an account that reduced it on the page of the appearance: see [motion.md](motion.md). Nothing flashes. |
| A part of a page that stopped following because of an error | It says so as an alert, with the reference of the error. |
| A page that is on its way for long | A line that is not shown says "Loading the page." to a screen reader, while the placeholder shows: see "The placeholder" in [motion.md](motion.md). |
| The server being rejoined | The dialog has the name "Connection to the server", and what it says is read out as it changes. The seconds that it counts down are not read out one by one. |
| An error that nothing handled | What the page says of it is an alert, and what puts it away is a button. |
| A session that is about to end | A dialog says so shortly before, with when, and "Stay signed in" starts the period anew: see "The notice before a session ends" in [accounts.md](accounts.md) (WCAG 2.1, 2.2.1). |

**Two limits of time are left as they are, and say so.** A link in an email holds for the time that the email and its page name, and a new one is asked for where it has run out. What a user proved again before a sensitive action holds for the minutes that the page which asks for it names; after them the page asks again, and a key that was typed is not kept, because a key is never written back into a page.

## The check of every screen

A screen is checked in two ways. The first is a part of every run of the tests. The second drives a browser, and a run starts it when it is told to.

### The list of screens

`AccessibilityRules.States` in `tests/Ecnanib.Tests/Accessibility` lists every state of every screen that looks different to a user: the route, a name, who opens it, how it is reached, and what shows that it was reached.

| Rule | Detail |
|---|---|
| **Every page is on the list.** | `AccessibilityRuleTests` finds the pages by their routes: a page that is added fails it until a state of the page is listed, and a state of a page that is gone fails it too. |
| **A state says how it is reached, in steps that both checks take.** | It is opened, or it is what a form answers with, such as a form whose every field is refused. One description serves a client over HTTP and a browser. |
| **A state says what shows it.** | A selector that is on the page only in that state. A state that is not reached fails by that, and is not passed over. |
| **One seeded application serves almost all of them.** | `AccessibilityWorld`: a visitor, a first-time user, a user whose address is not verified, a user with agents, cycles, trades, lessons, keys, and notifications, a user who turns two-factor authentication on, and an operator. One more user is there for the operator to find and disable: nobody is signed in as them, so that signs nobody out, and a visitor then gives their password and is told that the account is disabled. Its file holds a key of the service for one provider, so that the operator has a catalog to sync. The states in which registration is closed have an application of their own. |
| **What is left off the list is listed too, with why.** | `AccessibilityRules.LeftOff`: what a form says after too many attempts or too many wrong codes, which would lock the people of the list out, an email that could not be sent, the sentence in place of the button that syncs the catalog where the file holds a key for no provider, and the page of an error in place of a request that failed. Each is said in the same way as a state that is on the list. |

**To add a screen,** add each of its states to the list, with the fixture that seeds what it shows. **To add a state of a screen that there is,** add it after the states that it would change.

### The markup of every state

`AccessibilityMarkupTests` asks for every state once and holds its markup to the rules of `AccessibilityRules.Markup`, one test for each rule, which names every state that breaks it.

| Rule | WCAG 2.1 |
|---|---|
| The page says its language, has a title, and has one `<h1>`. | 3.1.1, 2.4.2, 1.3.1 |
| No level of heading is left out. | 1.3.1 |
| An id is on a page once, and every id that an attribute names is there. | 4.1.1, 1.3.1 |
| Every field has a name, and choices of one question stand in a `<fieldset>` with a `<legend>`. | 1.3.1, 3.3.2, 4.1.2 |
| Every link, button, and `<summary>` has a name, and a name that is given apart holds the text that shows. | 2.4.4, 4.1.2, 2.5.3 |
| Only a control is a stop of the keyboard, or a named part that scrolls, and none has a place of its own in the order. | 2.1.1, 2.4.3 |
| Every table has a name, a scope on each header, and a header in each row. | 1.3.1 |
| The parts of a page are named, and no two of a kind share a name. | 1.3.1, 2.4.1 |
| Every picture has a name or is hidden. | 1.1.1 |
| A field that is marked as wrong names what says why, and what says why is named by a field. | 3.3.1, 3.3.3 |
| At most one element asks for the focus, none in a dialog, and none beside what stands above a form. | 2.4.3 |
| What a form is answered with says so to a screen reader. | 4.1.3 |
| A link that opens in a new tab says so. | 3.2.5 |
| Nothing is said in a `title`. | 4.1.2 |
| The address, the password, and the code of a user say what they are for. | 1.3.5 |
| Every dialog has a name, every part that opens has a `<summary>`, and nothing that is hidden from a screen reader is a stop of the keyboard. | 4.1.2 |
| Nothing keeps a page from being enlarged. | 1.4.4 |

Each rule has a page that breaks it, which the tests plant, so a rule that could not fail shows. **To add a rule,** add it to the list with its criterion, its check, and a planted page.

`AccessibilityStyleTests` holds the stylesheet to what the markup cannot say: that what has the focus is outlined, with a contrast of 3 to 1 or more on the page of each theme, that the outline is taken away only where the list says, and that the line around a control which the list names reads on both pages.

### The run in a browser

`AccessibilityBrowserTests` opens every state of the list in Chromium, on the application as a browser reaches it. It is skipped unless the run is told to drive a browser, as the tests that call a provider for real are:

```
ECNANIB_BROWSER=chromium dotnet test --solution Ecnanib.slnx --filter-class "*AccessibilityBrowserTests"
```

A machine that has no Chromium of the version that the tests ask for gets it with `ECNANIB_BROWSER_INSTALL=1` beside it, once. The run reaches no other network.

| What it does with each state | Detail |
|---|---|
| **It has axe check the page.** | The rules of WCAG 2.0 and 2.1, levels A and AA, on a light page, and the rules of colour again on a dark one. A rule that is left out is listed in `AccessibilityRules.AxeWithout` with why: none is. |
| **It walks the page with the keyboard.** | It presses Tab from where the focus is when the page opens until it has been round. Every control of the page is a stop, once, in the order of the page; nothing else is; the keyboard gets past every one; and each stop shows the line of the focus, with a contrast of 3 to 1 or more against what is behind it. What has the focus first is what "When a page opens" says. |
| **It looks for a page that scrolls sideways.** | At the width of a phone, and at the width that is a desk at twice the size. |
| **It listens to the console.** | A report of the content security policy, or an error of a script, is a finding. A page that is shown in place of an address which has none, or is not for its user, is answered with the status that says so, and the browser notes that: it is what the state is, and no finding. |

It goes through the states once, in the order of the list, each person in a browser of their own: a state may use up what it shows, so it is one run and not a test for each state. It takes about three minutes. What axe could not decide is written down for a person to look at, and fails nothing.

Once in a run it also follows the link past the bar, opens and closes the menu of a narrow screen by keyboard, goes to another page without leaving the one that it is and looks where the focus is, sends a form that is refused and looks where the focus is, has the notice before a session ends come and answers it by keyboard, lets a session end, pauses a page that follows and has it follow again, and has a page ask for itself again while its heading has the focus. That takes a minute and a half more, most of it the wait for a session to end.

What a run finds is written beside the tests as they were built, to `tests/Ecnanib.Tests/bin/Debug/net10.0/TestResults/accessibility/findings.md`, with a picture of each state that something was found at. The run starts the application on a port of this machine, over TLS with a certificate that it makes for itself: `ListeningApplicationTests` holds that way of starting it with every run of the tests, so it works when a release needs it.

## Before a release

1. `dotnet test --solution Ecnanib.slnx`: every state of every screen keeps to the rules of the markup.
2. The run in a browser, as above: no finding of axe in either theme, a walk with the keyboard that is complete for every state, and no page that scrolls sideways.
3. By hand, what no test holds:
   - With a screen reader, VoiceOver in Safari or NVDA in Firefox: register, sign in, send a form with a field that is refused, create an agent, open its screen while a test cycle runs, pause the page and have it follow again, and mark a notification as read. Each page is said by its heading, each answer of a form is read out, and the team at work is not read out more than can be listened to.
   - The walk with the keyboard in Firefox and in Safari, on the screens of [layout.md](layout.md). In Safari, a link is a stop of the keyboard with Option and Tab.
   - The text alone enlarged to twice its size, in Firefox, on the dashboard and on the editor of an agent: nothing is cut off.
   - The pages with the colours of the device forced, on Windows: every state is still said in words.

## Testing

| Test class | What it holds |
|---|---|
| `AccessibilityRuleTests` | The list: that every page has a state on it, that every state is of a page that there is and says a path of its route, and that each has a name of its own, what shows it, and why it is listed. |
| `AccessibilityMarkupTests` | That every state on the list is reached and shows what it is listed for, and keeps to each rule of the markup. That a page which breaks a rule is found, a page that keeps to every rule is left alone, and every rule has a check and a planted page. |
| `AccessibilityStyleTests` | The line around what has the focus, where it is taken away, and the lines around the controls that the list names. |
| `AccessibilityBrowserTests` | When a run is told to: that every state is reached in the browser, axe on every state in both themes, the walk with the keyboard, the widths, the console, and what is done once in a run. That a planted page is found by axe and by the walk. |
| `AccessibilityPageTests` | What a page says takes the focus when it opens, that what stands above a form takes it and no field asks for it then, that a form which names fields as wrong gives it to the first of them, which names what says why, that a link which opens beside a form says so, and what the frame says to a screen reader that it shows nobody. |
| `ListeningApplicationTests` | That an application which listens answers over TLS on this machine and signs a user in, and that one which answers in memory listens nowhere. |
| `SessionNoticeTests`, `SessionScriptTests` | The notice before a session ends: see "The notice before a session ends" in [accounts.md](accounts.md). |
| `LivePageTests` | What pauses a page that follows: see "Testing" in [live-updates.md](live-updates.md). |
| `LayoutPageTests`, `ThemeStyleTests`, `MotionStyleTests` | The frame, the contrast of the colours that the stylesheet writes out, and that nothing moves for whoever reduced it: see [layout.md](layout.md), [themes.md](themes.md), and [motion.md](motion.md). |

## Not built yet

- A check with a screen reader that a test runs. It is by hand: see "Before a release".
- The run in a browser in Firefox and in Safari. It drives Chromium.
- Rules for a device that forces its colours. Every state is said in words, so nothing is lost there, and a mark that is drawn as a filled shape may not show.
- A key that is shown by its last four characters is written as four dots and those characters, which a screen reader reads as they are.
- Saying which fields of the editor of an agent may be left empty: every field of it but the model of a role is required, and the page says of the roles that one may wait for its model.
- A team at work that is read out less: a screen reader reads a role when it changes, and the step of the cycle with it.
- The key that sends a form from a field of the editor of an agent presses the first button of the form, as a browser does, which shows the symbols of the market and stores nothing. The button that saves is reached by the keyboard as every button is.
- A part of the strengths of a role that a screen reader names once: the section and the table that scrolls in it have one name.
- A time as an element that says its moment to a browser, `<time datetime>`. A time is words, in the time zone of the page, with how far that is from UTC: see [times.md](times.md).
- A notice before a session ends for a page without the script, and a notice before what a user proved again runs out.
