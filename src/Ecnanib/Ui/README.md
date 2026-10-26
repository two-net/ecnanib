# Ui module

The web user interface's shared pieces (SRS §3.1.1 UI-01..04; §3.2 FR-UI-01..06; NFR-USE-01..03; NFR-PERF-03; C-06,
C-07; Appendix D "ui"). The pages themselves live in `Components` next to the module they belong to; this folder holds
what every page shares: the `Ui` configuration section and the theme and motion contract between the server and the
stylesheet. Other modules depend on `Ecnanib.Ui.Abstractions`, `UiOptions` (the accounts module and the pages read the
defaults) and `AddUi(...)`, which requires nothing and must be registered before `AddAccounts` (new accounts take its
defaults). A page under `Components/<Module>` is part of that module and may use its internals
(`Components/Account/_Imports.razor` imports the accounts module's session helpers for its pages alone); the shared
components (`Layout`, `Live`, `Pages`) see the other modules' contracts only (NFR-ARCH-02; the root `README.md` states the
rule and `ArchitectureTests` enforce it).

| Need | Use |
|---|---|
| The theme and animation defaults for a visitor or a new account (FR-UI-02, FR-UI-03) | `IOptions<UiOptions>.Value.Defaults` → `UserPreferences(Theme, ReducedMotion)` |
| A user's own preference (persisted per user, FR-UI-02) | the accounts module: `IAccountService.GetPreferencesAsync` / `UpdatePreferencesAsync` |
| What the document element carries before the first paint | `ThemeAttributes.For(preferences, transitionMilliseconds)` → `data-theme` (absent for System), `data-motion` (`reduced` or absent), the `--ui-duration-base` style token; `App.razor` renders them |
| Switch without a reload (FR-UI-02) | the `PreferencesPanel` island on `/settings` saves and sets the same attributes on `<html>` through a few lines of JavaScript |
| Write an instant on a page (NFR-USE-03) | `UtcStamp.Minute(at)` / `UtcStamp.Second(at)`: the UTC value in the invariant culture, "never" for null |

Rules the module enforces:

- Theme follows the operating system when it is System: the stylesheet declares `color-scheme: light dark` and every
  colour is a `light-dark()` token, so no script runs for a live change; `data-theme="dark"` or `"light"` pins the
  `color-scheme` instead. `Ui:DefaultTheme` defaults to System, as FR-UI-02 requires, and is bound as text so a
  misspelling refuses to start (FR-CFG-06) instead of throwing out of the binder.
- Motion (NFR-USE-02, FR-UI-03), one mechanism: `prefers-reduced-motion: reduce` and `data-motion="reduced"` both set
  `--ui-duration` to zero and stop every `.anim-loop` animation (the element, its descendants and their `::before` and
  `::after`) with `!important`; neither can force motion back on. Every transition and every finite animation reads
  `var(--ui-duration)`, which the stylesheet derives from the inline `--ui-duration-base` (`Ui:TransitionMilliseconds`,
  at most 400; an inline value could not be overridden, the derived one can), so nothing outlasts 400 ms; every infinite
  animation (the hero loop, the reconnect spinner, the "evaluating" and "thinking" pulses) lives under an `.anim-loop`
  element and looks right at rest. The two scripts that move things, the deliberation replay and the chart, check both
  signals: the replay skips to its end, the chart sets its scroll position instead of animating it and turns kinetic
  scrolling off. Everything an animation conveys is also in text (status pills, timelines, the deliberation turns, the
  chart's legend and the trades section), and the in-app toggle is the way to stop what loops while an evaluation runs.
  `DesignContractTests` pins all of it.
- Accessibility (NFR-USE-01, WCAG 2.1 AA), structurally: `<html lang="en">`, one `<main>` with a skip link to it, one
  `<h1>` and no skipped heading level per page, a named landmark navigation, a specific `<title>` per page (the agent's,
  the key's name), unique ids; every control has a label (a `<label>` wrapping it or named by `for`, one label per
  control, `aria-label` in the council table where the row and column headers are the visible label), every button and
  link a name, every table a caption or `aria-labelledby`, every header cell text (`.visually-hidden` where the column
  holds only actions), every inline SVG `aria-hidden` with a caption, the chart canvas `aria-hidden` (its attribution link out of the tab order)
  with a hidden sentence, the legend and the trades section as its text; live regions never nest and exist before their text arrives (the chart
  badge); the reconnect dialog is a named `alertdialog` whose state text is a live region, whose Retry/Resume button takes
  focus when it appears and which Escape cannot close over a dead page; Blazor's error bar is an alert with a real Dismiss
  button; no page reloads itself unasked (the status page reloads only after "Reload every minute" is followed).
  Visually: every colour is a token in `tokens.css` and every pair the design uses meets 4.5:1 for text and 3:1 for the
  focus ring, control borders and the chart in both schemes; the `:focus-visible` ring is never removed (validation state
  is an inset shadow, not the outline; the programmatic focus on the `h1` after navigation is the one exception); status
  never rests on colour alone (text in every pill, hollow up and filled down candles, "(completed)" on finished wizard
  steps); `forced-colors` turns the background cues into borders; a browser without `light-dark()` gets the light palette
  from an `@supports not` block. The structural rules are a checker in the tests (`AccessibilityChecks`) that every
  anonymous page passes offline and every signed-in page passes in the Live tour, with seeded content on every branch.
- English (NFR-USE-03): the UI has one language and the process has one culture: both projects set
  `InvariantGlobalization`, so every culture-sensitive format and parse is invariant whatever the host's locale and no
  ICU is needed; pages write instants through `UtcStamp`, numbers through the invariant formatters of the `*Messages`
  classes, and the chart labels its axis in `en-US`. `CultureTests` pins the mode and forbids the host's clock and culture
  in the source.
- Stylesheets: `wwwroot/css/tokens.css` (tokens, the fallback palette, theme and motion switches), `base.css` (reset,
  typography, layout, focus), `components.css` (cards, pills, buttons, forms, tables, stepper, timeline, animation
  primitives, forced colours) and `pages.css` (page-specific rules), linked in that order from `App.razor` through
  `@Assets` so each is fingerprinted; no `@import` or cross-file `url()`, which the asset pipeline does not rewrite.
  Scoped `.razor.css` files consume the tokens and never select on `data-theme`.
- Live updates (FR-UI-04, NFR-PERF-03): pages stay statically rendered with plain forms; the parts that change without
  a reload are interactive server islands under `Components/Live` that subscribe to `IAgentEvents` and the market
  streams for the signed-in user only (NFR-SEC-03): `DashboardLive`, `AgentHeaderLive`, `PositionsOrdersLive`,
  `DecisionsLive` (with `DeliberationSequence`), `TokenUsage`, `TradeTimeline`, `PriceChart` and `DeliberationReplay`,
  plus the account module's `PreferencesPanel`. They share `LiveComponentBase` (one event subscription per island,
  refreshes coalesced by `RefreshCoalescer` after a 250 ms debounce, disposed with the circuit) and `PriceFeed` (one
  price stream per symbol, at most one update a second). The latency budget of NFR-PERF-03 is 2 s from an event to its
  render: the event reaches the island on the thread pool at once, the debounce adds 250 ms, then one re-read and one
  render; the coalescer measures that span on the server (the SignalR hop to the browser excluded) and logs a Warning
  naming the island when it is exceeded, Debug otherwise. Islands that render from the event's own data skip the
  debounce. The chart is TradingView's Lightweight Charts, vendored under `wwwroot/lib` (Apache-2.0, see
  `THIRD-PARTY-NOTICES.md`); nothing is loaded from a third-party origin (NFR-SEC-04). The landing hero's loop is CSS
  only and pauses with a checkbox.
- Cost (NFR-COST-01): the agent page's `TokenUsage` island shows the council's decisions and tokens all-time and today,
  the output-token cap new calls are made under and, per role, the calls, tokens and reasoning tokens, re-read when a
  decision is recorded or an evaluation finishes; the deliberation page's cost lines show each turn's latency, tokens
  and attempts and the decision's totals, every number with thousands separators; the keys page and the usage card say
  that provider and exchange costs are the user's under BYOK.

Configuration (`Ui` is this module's section):

```json
{ "Ui": { "DefaultTheme": "System", "ReducedMotion": false, "TransitionMilliseconds": 240 } }
```

Pages: none of its own; the design system is used by every page.

Known gaps: `light-dark()` and `:has()` need a current browser (Baseline 2024); older browsers see the light palette and
cannot pause the hero. Under 40rem a `.stack` table lays its rows out as blocks with the header row visually hidden; a
browser may stop exposing it as a table then, though the column names stay in the cells' labels. The council table's
`aria-label`s repeat the role names the row shows. The metric columns' definitions are tooltips (`title`) on the headers.
The chart's candle data has no table of its own; the legend and the trades section are its text.

Tests: `dotnet test` runs offline (options and validation, registration, the theme attributes for a visitor through the
pipeline, `UtcStamp`, the live islands' texts (`LiveMessagesTests`, the usage card's among them), the refresh coalescer
under a fake clock: coalescing, the trailing read, the latency and its budget;
`AccessibilityChecksTests` prove every rule of the checker fires; `AccessibilityTests` tour the anonymous pages;
`DesignContractTests` compute the contrast of every token pair and scan the stylesheets and scripts for the focus, colour
and motion rules; `CultureTests` pin the invariant mode). Live, against PostgreSQL: `AccessibilityLiveTests` seed an account
with every kind of content and tour every signed-in page (the usage card and the deliberation's cost lines among the
markers); `ThemeAttributesLiveTests` save a reduced-motion dark preference
and read it back on the document element.
