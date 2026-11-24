# Themes

The user can choose Dark, Light, or System, and System is the default for new users and for visitors (FR-THM-1). System follows the color-scheme preference of the device and switches live when that preference changes (FR-THM-2). The choice is saved to the account of a user and applied on every device that they sign in on, and for a visitor it is saved on the device (FR-THM-3). The correct theme is applied before the first paint, with no flash of the other theme (FR-THM-4). Every screen and chart works in both themes, with a text contrast that meets WCAG 2.1 AA, and a profit and a loss are told apart by their sign (FR-THM-5).

The theme of an account is `Theme`, `IThemeChoice`, and the `theme` column of `users`, in `src/Ecnanib/Accounts`. What decides the theme of a page, and the cookie of a visitor, are `PageTheme` and `DeviceTheme` in `src/Ecnanib/WebUi/Themes`, with the fields of the choice in `ThemeChoices.razor` there. The two pages are `Appearance.razor` in `src/Ecnanib/WebUi/Pages/Account` and `DeviceAppearance.razor` in `src/Ecnanib/WebUi/Pages`, with their texts in `AppearancePages.cs`. The look is in `src/Ecnanib/wwwroot/app.css`. The rules below are enforced by `ThemePageTests` and `ThemeStyleTests` in `tests/Ecnanib.Tests/Themes`.

The frame that every page is written in is in [layout.md](layout.md), how a page is kept from carrying a script or a style of its own is in [security.md](security.md), and what is stored about a person is in [accounts.md](accounts.md).

## How a page gets its theme

**No script has a part in it.** The server decides the theme of every page that it answers with, and writes it into the page. The stylesheet does the rest.

| Step | What happens |
|---|---|
| 1. The server decides. | `PageTheme.Of`: for a user who is signed in, the theme of their account. For a visitor, the theme that their device saved. Whoever has chosen nothing has System. |
| 2. The page says it, first of all. | `App.razor` writes `<html lang="en" data-theme="dark">` or `data-theme="light"`, and no attribute for System. Ahead of the stylesheets it also writes `<meta name="color-scheme">` with `dark`, `light`, or `light dark`, which tells the browser what it may paint before a stylesheet is there. |
| 3. The stylesheet follows. | `:root` has `color-scheme: light dark`, `:root[data-theme="light"]` has `light`, and `:root[data-theme="dark"]` has `dark`. The browser takes its own colours for the page, the text, the links, and the controls from that. |

| Rule | Detail |
|---|---|
| **The right theme is there before the first paint (FR-THM-4).** | It is in the first element of the page as the server sends it, and the stylesheets are named in the head, so a browser paints nothing before it has them. There is no moment at which a script would have to change the page, and so nothing to flash. |
| **System follows the device, live (FR-THM-2).** | A page that says nothing may be light or dark, and the browser takes the one that the device is set to. When the setting of the device changes while a page is open, the browser paints the page anew by itself. |
| **A page that is asked for without leaving it follows too.** | The framework changes what differs between two pages, the first element included. So a theme that was changed on another device is on this one with the next page, however it is opened. |
| **Every page has it.** | A page of a user, a page of a visitor, the page of an error, and the page that says that there is no page at an address: each is written by `App.razor`. |
| **The content security policy is as it was.** | No script and no style is written into a page for a theme: an attribute, and a `<meta>` element. `BrowserDefenseTests` opens every page as before. |

## Choosing a theme

There are two pages, one for each of the two places that a choice is saved in. Both offer System, Dark, and Light, in that order, each with what it means below it, and the one that holds now is ticked.

| | A user | A visitor |
|---|---|---|
| Page | `/account/appearance`, the appearance among the settings (FR-UI-2) | `/appearance` |
| What leads to it | "Settings" in the bar, and "Appearance" in the navigation of the settings. The page of the account names the theme that is stored and leads there too. | "Appearance" at the foot of every page of a visitor, under the legal documents. |
| Where the choice is saved | The `theme` column of the row of the account. | The cookie `__Host-ecnanib-theme`, on the device. |
| Where it holds | On every device that the user signs in on, from the next page on. | In that browser. |

| Rule | Detail |
|---|---|
| **A new user and a visitor have System (FR-THM-1).** | A new row of `users` says `System`, and so does every account that there was before the themes: the migration `AddTheme` gives them that. A device with no cookie is System. |
| **The form is posted to its page, with the token of the page.** | As every form: see "Request forgery" in [security.md](security.md). The answer leads back to the page, by a request that changes nothing, and that page is already in the theme that was chosen. |
| **The form names no user.** | The choice is of whoever is signed in: `IThemeChoice.SetAsync` takes the user from the session (NFR-SEC-3). |
| **A form that names no theme saves nothing.** | The page comes back as it was, and says "Choose System, Dark, or Light." |
| **The choice asks for no proof of identity, and is in no event of the audit trail.** | It exposes no key and moves no money, as the choice of email for critical events. |
| **A user who opens `/appearance` is sent to `/account/appearance`.** | The pages of a user follow their account, so that is where their choice counts. A visitor who opens `/account/appearance` is sent to sign in, as on every page of the settings. |
| **The frame has no form for it.** | The foot of a visitor has a link, and the settings of a user have a page. So a page that offers nothing to change still has no form but the button that signs out, and a page of a visitor that had no form has none. |

## The account and the device

| Rule | Detail |
|---|---|
| **While a user is signed in, the theme of their account counts.** | Whatever the device has saved. |
| **Signed out, the device has its own choice again.** | Signing in and signing out neither set the cookie of the device nor take it back. So a shared device does not keep the theme of whoever was signed in last. |
| **A request is told the theme of its user as it is signed in.** | The session of a request is looked up on every request, and the theme of the account is read in that same query: no page asks the database again for it. It is a claim of the user of the request, `SessionAuthentication.ThemeClaimType`, which `SignedInUser.ThemeOf` reads. See "How a request is signed in" in [accounts.md](accounts.md). |
| **The theme is a preference, and personal data of an allowed kind (NFR-SEC-9).** | It is one of three names, and cannot say who someone is. It is in the export of the user's data, under `account`, and goes with the account when it is deleted. |

### The cookie of a visitor

| | |
|---|---|
| Name | `__Host-ecnanib-theme` |
| Value | `dark` or `light`. A device that looks as it is set has no cookie: choosing System takes it back. |
| Attributes | `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, and `Max-Age` of one year, which starts anew when a choice is saved. |
| Who reads it | The server alone, to write the theme into a page. So it is hidden from scripts, as every cookie is. |
| What it says | Nothing of who the visitor is, and nothing is stored about a visitor for it. |

A cookie that says anything but `dark` or `light` is no choice: the page is as the device is set, and what the cookie says is written into no page.

## Colours

**A page has the colours of the browser,** for the page itself, its text, its links, and its controls: the browser gives each the value of the theme, with a contrast that meets WCAG 2.1 AA. The stylesheet adds few colours of its own.

| A colour in `app.css` is | For instance |
|---|---|
| The colour of the text around it, whole or thinned | The lines of the frame and of the tables, the equity curve, the cards and the lines of the team at work, the outline of the Demo badge: `currentColor`, and `color-mix()` with it. |
| A colour of the system | `Canvas` and `CanvasText`, for the link past the bar. |
| A colour of the themes | `--danger`, for the error of a form, the line beside what a user is told, and the outline of a role of the team that has failed, and `--warning`, for the line beside what a form warns of. |
| One that brings its own background | The Live badge: white on red, the same in both themes. |

| Rule | Detail |
|---|---|
| **A colour of the themes is said once, with a light value and a dark one.** | `--danger: light-dark(#b3261e, #f2b8b5)` in `:root`. `light-dark()` follows `color-scheme`, so the value is the one of the theme of the page, and of the device where the page says nothing. A part of a page that is light in both themes, such as the dialog that rejoins the server, gets the light value by the same rule. |
| **No rule asks the device what it is set to.** | `@media (prefers-color-scheme: dark)` would hold for a user who chose Light on a dark device. `ThemeStyleTests` fails for a stylesheet that has one. |
| **Each value reads on the page of its theme.** | With a contrast of 4.5 to 1 or more, which `ThemeStyleTests` computes: the light value on white, and the dark value on `#1e1e1e`. A browser paints a dark page in a grey of its own: Chrome and Edge `#121212`, Firefox `#1c1b22`, and Safari `#1e1e1e`. The test takes the lightest of them, and there is room to spare: the two colours of today have 7.4 and 9.8 to 1 there. |
| **A colour that is written out anywhere else is found.** | `ThemeStyleTests` lists the rules of `app.css` that write out a colour: `:root` and `.mode-live`. A rule that is added with a colour of one theme fails it. |
| **Nothing relies on a colour.** | A profit and a loss are told apart by their sign, and have no colour of their own (FR-THM-5): see [dashboard.md](dashboard.md) and [agent-screen.md](agent-screen.md). An outcome, a state, and a mode are said as words. |
| **What must not change with the theme does not.** | The QR code of two-factor authentication is black on white whatever the theme is, so an authenticator app reads it. The dialog that rejoins the server and the bar of an unhandled error are light in both themes, with colours of their own: `color-scheme: light only`. |

### Adding a colour

1. Use the colour of the text, `currentColor`, where that says enough: a line, an outline, a chart.
2. Otherwise add a colour of the themes to `:root` in `app.css`, with a light and a dark value, and use it with `var()`.
3. Add its name to `ThemeStyleTests.A_colour_of_the_themes_reads_on_the_page_of_each`, which then holds both values to the contrast.

## Browsers

`light-dark()` is in Chrome and Edge since version 123, in Firefox since 120, and in Safari since 17.5, all of 2024 or before, which is within the current and the previous major version of each (SRS 2.3). `color-scheme` and `<meta name="color-scheme">` are older. A browser from before `light-dark()` shows the errors of a form, and the lines beside a warning and beside what a user is told, in the colour of the text: a line is first a line in that colour, and is then given its own.

## Testing

| Test class | What it holds |
|---|---|
| `ThemePageTests` | That a visitor and a new user have System, and an account from before the themes too. That a user chooses each theme and every page is written in it, ahead of its stylesheets and without a script. That the theme of an account holds on every device and is not saved on the device, is on the page of the account and in the export, and is not the choice of another user. That a visitor chooses on a page of their own, with the cookie and its attributes, and that System takes the cookie back. That a cookie which names no theme is no choice. That the account counts while its user is signed in and the device otherwise. That a form which names no theme, or has no token, saves nothing. That the foot leads a visitor to the page, and the settings a user. |
| `ThemeStyleTests` | The three rules of `color-scheme`, that no stylesheet asks the device, the contrast of each colour of the themes, of the Live badge, and of the button of the dialog that rejoins the server, and which rules write out a colour. |
| `LayoutPageTests` | That the page of the appearance is among the settings, with its form, and that the foot of a visitor leads to theirs. |
| `DataExportTests` | That the theme is in the export, under `account`. |
| `BrowserDefenseTests`, `RequestForgeryTests`, `LivePageTests` | That the two pages carry no script and no style of their own, check the token of their form, and keep no connection: each finds the pages by their routes. |

No test drives a browser. By hand:

1. `docker compose up -d`, and run the application.
2. As a visitor, open `/` with the device set to light, and set the device to dark while the page is open: the page follows at once. The same on a page of a user who has System.
3. Open "Appearance" at the foot, choose Dark, and save. Every page is dark, also with the device set to light. Reload a page with the network slowed down and the cache off: nothing of it is light at any moment. Choose Light, and the same holds the other way round. Choose System, and the cookie is gone.
4. Register, sign in, and choose Dark in the settings. Sign in on another browser: the first page there is dark. Sign out: the page is as that device chose.
5. Walk the screens of [layout.md](layout.md) in both themes, at the width of a phone and of a desk: the text, the lines, the badges, the errors and warnings of a form, the equity curve, the QR code, and the dialog that rejoins the server.
6. Do the same in Chrome or Edge, in Firefox, and in Safari. The console of the browser shows no report of the content security policy.

## Not built yet

- A preference for reduced motion (FR-ANI-4), which the specification stores with the account beside the theme. What moves is the team at work on the screen of an agent, and it follows what the device asks for: see "What moves" in [agent-screen.md](agent-screen.md).
- A colour for a profit and one for a loss. They are told apart by their sign, which the specification asks for, and by nothing else.
- Taking the theme that a visitor chose on their device into the account that they then register. A new account has System.
- A check of every screen by a tool that measures contrast in a browser (NFR-USA-1). The contrast of the colours that the stylesheet writes out is computed by a test, and the rest are the colours of the browser.
