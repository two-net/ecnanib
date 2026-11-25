# Layout and screens

The UI is a responsive web application, usable at desktop and mobile widths (FR-UI-1). There is a route and navigation for each screen: landing, registration and sign-in, dashboard, agent list, agent editor, agent detail, experience, model catalog, settings (profile, security, API keys, appearance, notifications), and operator status (FR-UI-2). The UI works in the current and previous major versions of Chrome, Edge, Firefox, and Safari (SRS 2.3).

This is the frame that every page is written in, and the map of the screens. What a screen shows is delivered by the feature that it belongs to, and is in the document of that feature.

The frame is `MainLayout.razor` in `src/Ecnanib/WebUi/Layout`, with `ScreenLink.razor`, `SettingsNav.razor`, and `NotificationsLink.razor`. What each screen is called and where it is, is `Screens.cs` there, and the paths are in `WebUiModule`. The start page is `Home.razor` in `src/Ecnanib/WebUi/Pages`, with its texts in `HomePages.cs`. The look is in `src/Ecnanib/wwwroot/app.css`. The rules below are enforced by `ScreenRuleTests` and `LayoutPageTests` in `tests/Ecnanib.Tests/Layout`, with the map in `ScreenRules.cs` there, and by `AuditRulesTests` in `tests/Ecnanib.Tests/Accounts`.

How a page is kept from carrying a script or a style of its own is in [security.md](security.md), how a page follows what changes in [live-updates.md](live-updates.md), how a page gets its theme in [themes.md](themes.md), what moves on a page in [motion.md](motion.md), and who is an operator in [ownership.md](ownership.md).

## The screens

| Screen (FR-UI-2) | Page | For | What leads to it |
|---|---|---|---|
| Landing | `/` | Everyone | The name of the service in the bar, from every page. |
| Registration and sign-in | `/register`, `/sign-in` | Visitors | The bar of a visitor, and the start page. See [accounts.md](accounts.md). |
| Dashboard | `/dashboard` | Users | The bar, and the start page. See [dashboard.md](dashboard.md). |
| Agent list | `/agents` | Users | The bar. See [agents.md](agents.md). |
| Agent editor | `/agents/new`, `/agents/<id>` | Users | The list of the agents: "Create an agent", and the name of each agent. |
| Agent detail | `/agents/<id>/state` | Users | The list of the agents, and the dashboard. See [agent-screen.md](agent-screen.md). |
| Experience | `/experience` | Users | The bar. See [experience.md](experience.md). |
| Model catalog | `/catalog` | Users | The bar. See [catalog.md](catalog.md). |
| Settings: profile | `/account` | Users | "Settings" in the bar, and "Profile" in the navigation of the settings. |
| Settings: security | `/account/two-factor` | Users | The navigation of the settings. |
| Settings: API keys | `/account/keys` | Users | The navigation of the settings. See [credentials.md](credentials.md). |
| Settings: appearance | `/account/appearance` | Users | The navigation of the settings. See [themes.md](themes.md), and [motion.md](motion.md) for how much of the pages moves, which is chosen there too. |
| Settings: notifications | `/account/notifications` | Users | The navigation of the settings. See [notifications.md](notifications.md). |
| Operator status | `/operator` | Operators | The bar of an operator. |

| Rule | Detail |
|---|---|
| **The map is in one place, twice.** | `Screens.cs` is what the navigation is written from: the name of each link, its path, and the paths that are a part of its screen. `ScreenRules.cs` is what the tests hold the application to: each screen of the specification with its route, whom it opens for, and the page and the link that lead to it. A screen that is moved shows in both. |
| **Every screen has a page that says whom it opens for.** | A page of a user carries `[Authorize]`, and the operator status screen the operator policy. A visitor who asks for either is sent to sign in, and from there back. |
| **Every screen is led to by a link that whoever it opens for is shown.** | And by none that anyone else is: a visitor is not shown the way to a page of a user, and a user who is not an operator is not shown the operator status screen. |
| **The addresses of the pages that there were are as they were.** | The settings are the pages under `/account`, and an email leads to `/account/notifications`. No page was moved for the map. |
| **The profile is the page of the account,** | which says who is signed in, what is stored about them, and where the account is deleted. **The security is the page of two-factor authentication,** which is what there is of it. |
| **The bar leads to more than the specification names:** | the AI usage of a user, `/usage`, and their notifications, `/notifications`, with how many are not read. |
| **The foot leads a visitor to one page more:** | `/appearance`, where a visitor chooses the theme of their device. A user chooses the theme of their account among the settings, and is sent there from that page. See [themes.md](themes.md). |

## The frame

Every page is written in the same frame, for a visitor and for a user, also the page of an error and the page that says that there is no page.

| Part | What it is |
|---|---|
| The link past the bar | "Skip to the content", the first link of every page. It is out of sight until it has the focus. |
| The bar, `<header>` | The name of the service, which leads to the start page, and the navigation: see "The bar". |
| The content, `<main id="content">` | The navigation of the settings, on a page of the settings, and the page itself, which starts with its one `<h1>`. |
| The foot, `<footer>` | The Terms of Service, the Privacy Policy, and the risk disclosure, for a visitor and for a user. Under them, for a visitor, "Appearance". |

| Rule | Detail |
|---|---|
| **It is rendered on the server for each request, and no script of it keeps a connection.** | A page that follows what changes has its one part for that, and a page that follows nothing has none. See [live-updates.md](live-updates.md). The one script of the application is for what moves on a page, and the frame works without it: see [motion.md](motion.md). |
| **The link past the bar names its page.** | The pages share one base address, so a link to a part of a page says the address of the page too, with what follows its question mark. The browser follows it by itself, and not the framework: what a keyboard reaches next is then in the content. The content takes the focus for that, and is no stop of the keyboard otherwise. |
| **A page that opens takes the focus at its heading,** | as before: `FocusOnNavigate` in `Routes.razor`, for a page that is opened by a link and for one that is loaded anew. So the next stop of a keyboard is in the content, and every page has an `<h1>`, also the page that says there is none at an address. The link past the bar is for whoever goes back to the top of a page, or reads it from the top. |
| **Its texts are in one place.** | `Screens` (NFR-USA-4): the names of the links, of the menu, and of the navigations as a screen reader says them. |
| **A page carries no style of its own.** | The look of the frame is in `app.css`, with the look of every page. |

## The bar

| Rule | Detail |
|---|---|
| **A visitor is shown two ways in:** | "Sign in", and "Register" while registration is open. The bar of a visitor has no form and no menu. |
| **A user is shown their screens,** in this order: | Dashboard, Agents, Experience, AI usage, Model catalog, and Settings. Ahead of them is the link to the notifications, and who is signed in, by their email address, with the button that signs out. |
| **An operator is shown the operator status screen** as the last of them. | The bar asks the policy that the screen itself requires, with `AuthorizeView`: the link is shown to whom the screen opens for, and the Web UI does not ask who is an operator in a way of its own. See "Operators" in [ownership.md](ownership.md). |
| **The link to the screen that is open says so.** | `aria-current="page"` on the link to the very page that is open, and `aria-current="true"` on the link to a screen that the page is a part of: "Agents" on every page of an agent, "Experience" on the lessons, "Settings" on every page of the settings. A screen reader says it, and the link is heavier and underlined: it is told by more than a colour. `Screens.CurrentOf` says which, from the address of the page. |
| **The name of a link is its text, and nothing else.** | No icon, and no element inside it. The names are those of the pages: `DashboardPages.Title`, `ExperiencePages.Title`, `UsagePages.Title`, `NotificationPages.Link`. |
| **The button that signs out is the one form of the bar.** | It is posted to an endpoint of its own, with the token of the page, so it works on every page: see [accounts.md](accounts.md). A page that offers no way to change anything has that form and no other. |

## The settings

The pages under `/account` are the settings. Each has the navigation of the settings above its heading: Profile, Security, API keys, Appearance, and Notifications, in the order of the specification.

| Rule | Detail |
|---|---|
| **The layout puts it there.** | `SettingsNav`, for a signed-in user on a page whose address is `/account` or under it: `Screens.IsOfSettings`. A page of the settings carries nothing for it, and a page that is added under `/account` has it. |
| **It says which of the settings is open,** | as the bar does. On the page that stores a key, "API keys" is marked as the screen that the page is a part of. |
| **"Notifications" there is the choice of email for critical events.** | What the service told a user is the link in the bar. The two are in navigations of two names, "Settings" and "Main", which a screen reader says. |
| **A page under `/account` that is for visitors has none.** | The page that says an account was deleted: nobody is signed in on it. |

## Narrow and wide

The frame changes at one width, 48rem, which is 768 pixels at the text size that a browser starts with. Below it a screen is narrow, as of a phone. From it on a screen is wide.

| Rule | Detail |
|---|---|
| **On a wide screen the bar of a user has two lines.** | The name of the service, the link to the notifications, and who is signed in with the button that signs out. Under them, the navigation. |
| **On a narrow screen it has one: the name, the notifications, and "Menu".** | The menu opens what it holds, under the bar and as wide as the screen: who is signed in with the button that signs out, and the links of the navigation, one under another, each as tall as a finger needs. Closed, neither shows, and neither takes room. |
| **The menu needs no script.** | It is a `<details>` element with its `<summary>`, which the browser opens and closes, also by keyboard, and of which it says to a screen reader whether it is open. It holds who is signed in and the navigation. So the links are on a page once, and the page has no field to tick for it. |
| **On a wide screen the stylesheet shows what the menu holds, open or not,** | and hides what opens it: `.site-menu::details-content`, laid out as parts of the bar. No rule of the look reads whether the menu is open from an element beside it: Safari's engine did not always apply such a rule when the menu was opened or closed by a pointer. |
| **A browser that cannot lay out what a menu holds keeps the menu at every width.** | One from before 2025: the rules above are inside `@supports selector(::details-content)`. There the menu is a line of its own under the bar, and opens as on a narrow screen. |
| **A page that is opened from the menu has it closed.** | The framework changes what differs between two pages, and a page is written with its menu closed. |
| **A visitor has no menu.** | Two links fit in the bar of a phone. |
| **Nothing is as wide as a number of pixels says.** | What does not fit goes to the next line whole. A paragraph is no wider than reads well, 75 characters, and a table is as wide as the page lets it be. A table that is wider than a narrow screen scrolls sideways by itself, also by keyboard, as before, so the page itself never does. A word that is longer than a narrow screen is wide breaks. |
| **The page is as wide as the device.** | `<meta name="viewport" content="width=device-width, initial-scale=1.0">` in `App.razor`. A control is in the typeface and at the size of the text around it, so a phone does not enlarge the page when a field is typed in. |
| **The page is at least as tall as the screen.** | Its foot is at the bottom of a page that says little. |
| **The page is dark, light, or as the device is set.** | Its first element says which, and `color-scheme` follows it, with the typeface of the device: see [themes.md](themes.md). The frame has no colour of its own: a line is the colour of the text around it, thinned. |

## Browsers

The UI works in the current and previous major versions of Chrome, Edge, Firefox, and Safari. The frame needs no script of its own for that. The stylesheet keeps to what all four have had since 2023 or before, but for two rules, each of which an older browser does without.

| What the frame uses | Detail |
|---|---|
| `<details>` and `<summary>` | The menu of a narrow screen. |
| `::details-content`, inside `@supports selector()` | What the menu holds, shown on a wide screen whether the menu is open or not. Chrome and Edge have it since version 131, Safari since 18.4, and Firefox since 143. An older browser keeps the menu at every width. |
| Flexible boxes with `gap` | The bar, the navigations, and the foot. |
| `color-mix()` | The lines of the frame, as of the pages before it. |
| `color-scheme`, and the system colours `Canvas` and `CanvasText` | A page that is dark, light, or as the device is set. |
| `light-dark()` | The colours that have a light and a dark value: the error of a form, and the line beside a warning. Chrome and Edge have it since version 123, Firefox since 120, and Safari since 17.5. An older browser shows them in the colour of the text. See [themes.md](themes.md). |
| `100dvh`, after `100vh` | The height of a page on a phone, whose bar of addresses comes and goes. A browser that does not know the first keeps the second. |
| Logical properties, such as `margin-inline-end` | The sides of the frame. |

It uses no container query, no `:has()`, and no nesting.

## The start page

`/` is the landing page of a visitor, and the page that a user arrives at after signing in.

| Rule | Detail |
|---|---|
| **It says what the service is, in three sentences.** | AI trading agents on Binance, a team of four AI roles with the user's own keys inside fixed limits, and that Demo uses no real funds and Live can lose money, with a link to the risk disclosure. The AI providers and the exchange are named only to say which they are (NFR-LEG-4). |
| **It presents the team as an illustration** (FR-ANI-7). | Under what it says of the team: the four roles as the screen of an agent draws them, each with the provider that the configuration file gives it on a new agent, played once as one cycle. See "The illustration of the start page" in [motion.md](motion.md). |
| **A visitor is led to register and to sign in.** | While registration is closed, the page says so, and does not lead to it. |
| **A user is led to their dashboard and to their agents.** | The bar is around the page, as around every page. |
| **It has no form, stores nothing, and follows nothing.** | Its texts are in `HomePages`. It reads the names of the providers, for the illustration, and nothing else. |

**An address that has no page** is answered with "Page not found", with the frame around it, so there is a way on. A page that is not for the user who asked for it, such as the operator status screen, is answered with the same page and the status 403. The address of the browser stays the one that was asked for, and the link past the bar names that one.

## Screens whose feature is not built

One screen of the specification has a route and a place in the navigation, and says what it is for until its feature is built.

| Screen | What its page says | Its feature |
|---|---|---|
| Operator status, `/operator` | That it is for operators, what it is to show, and that it shows nothing of any user and nothing can be done on it. It has no form, and asks the database nothing. | The status screen and the controls of an operator (FR-OPS-2 to FR-OPS-5). |

**The operator status screen is no operator function.** It requires the operator policy, as an operator function does, and does nothing that the audit trail could record or that the Risk Guard would have to be told. `AuditRules.OperatorScreensThatOnlyShow` lists it with why, and `AuditRulesTests` holds it to that: the one form of its page is the button of the bar. The first form that is put on it fails that test, and anything else that requires the policy counts as an operator function as before. See "Operators" in [ownership.md](ownership.md).

## Testing

| Test class | What it holds |
|---|---|
| `ScreenRuleTests` | The map: that its screens are the ones of the specification, that each has a page at its route which says whom it opens for, that each is led to from the page and by the link that the map says, and opens there, and that nobody is led to a screen that does not open for them. |
| `LayoutPageTests` | The frame: that every page has the link past the bar, the bar, the content, and the foot, for a visitor and for a user, that the link past the bar names its page, which link the bar and the navigation of the settings mark on each screen, what a visitor is shown, the start page with registration open and closed, the foot of a visitor, the navigation of the settings and the pages that have it, the page of the appearance with its two forms, the menu of a narrow screen with what it holds and the rules of the stylesheet that it rests on, the operator status screen for an operator, a user, and a visitor, and the page of an address that has none. |
| `AuditRulesTests` | That a screen of an operator that only shows is listed, requires the policy, and has no form of its own, and that an operator function beside it is still seen. |
| `BrowserDefenseTests`, `RequestForgeryTests`, `LivePageTests` | That the new pages carry no script and no style of their own, check the token of a form, and keep no connection: each finds the pages by their routes. |

No test drives a browser. By hand:

1. `docker compose up -d`, and run the application.
2. Open `/` at the width of a phone and at the width of a desk, on a light and on a dark screen: the bar of a visitor, the start page with the illustration of the team, and the foot. The page does not scroll sideways at either width.
3. Register, sign in, and open each screen of the map at both widths. On the phone, "Menu" opens and closes the navigation, by a pointer and by the keyboard, in any order of the two, and a page that is opened from it has it closed. The link of the open screen is marked.
4. With the keyboard alone: a page that opens has the focus at its heading, so the first Tab is in the content. Shift and Tab from there go back through the bar to "Skip to the content", which shows while it has the focus. Enter follows it, and the next Tab is in the content again. In Safari, a link is a stop of the keyboard with Option and Tab.
5. Do the same in Chrome or Edge, in Firefox, and in Safari. The console of the browser shows no report of the content security policy.
6. List your address under `Application:Operators` in the configuration file, start again, and the bar has "Operator status".

## Not built yet

- The dashboard as the page that a user arrives at after signing in. A user arrives at the start page, which leads to the dashboard: `SessionAuthentication.LocalPathOrHome`.
- What the operator status screen shows, and what an operator does (FR-OPS-2 to FR-OPS-5). The Operator role itself is still to confirm (SRS 2.2).
- A page of the security that holds more than two-factor authentication: a change of the password by a user who is signed in, and the devices that they are signed in on. A password is changed by the link of "Forgot your password?".
- An accessibility check and a keyboard walk-through of each screen (NFR-USA-1). The frame has its landmarks, its link past the bar, and its marks of the open screen.
- The texts of the pages of the account and of two-factor authentication in one place, as the newer pages have them (NFR-USA-4).
