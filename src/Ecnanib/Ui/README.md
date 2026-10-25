# Ui module

The web user interface's shared pieces (SRS §3.1.1 UI-01..04; §3.2 FR-UI-01..06; NFR-USE-01..03; NFR-PERF-03; C-06,
C-07; Appendix D "ui"). The pages themselves live in `Components` next to the module they belong to; this folder holds
what every page shares: the `Ui` configuration section and the theme and motion contract between the server and the
stylesheet. Other modules depend on `Ecnanib.Ui.Abstractions` and `AddUi(...)`, which requires nothing and must be
registered before `AddAccounts` (new accounts take its defaults).

| Need | Use |
|---|---|
| The theme and animation defaults for a visitor or a new account (FR-UI-02, FR-UI-03) | `IOptions<UiOptions>.Value.Defaults` → `UserPreferences(Theme, ReducedMotion)` |
| A user's own preference (persisted per user, FR-UI-02) | the accounts module: `IAccountService.GetPreferencesAsync` / `UpdatePreferencesAsync` |
| What the document element carries before the first paint | `ThemeAttributes.For(preferences, transitionMilliseconds)` → `data-theme` (absent for System), `data-motion` (`reduced` or absent), the `--ui-duration-base` style token; `App.razor` renders them |
| Switch without a reload (FR-UI-02) | the `PreferencesPanel` island on `/settings` saves and sets the same attributes on `<html>` through a few lines of JavaScript |

Rules the module enforces:

- Theme follows the operating system when it is System: the stylesheet declares `color-scheme: light dark` and every
  colour is a `light-dark()` token, so no script runs for a live change; `data-theme="dark"` or `"light"` pins the
  `color-scheme` instead. `Ui:DefaultTheme` defaults to System, as FR-UI-02 requires, and is bound as text so a
  misspelling refuses to start (FR-CFG-06) instead of throwing out of the binder.
- Motion: `prefers-reduced-motion: reduce` and `data-motion="reduced"` both set `--ui-duration` to zero and stop every
  `.anim-loop` animation; neither can force motion back on. Transitions read `var(--ui-duration)`, which the stylesheet
  derives from the inline `--ui-duration-base` (`Ui:TransitionMilliseconds`, at most 400, FR-UI-03); an inline value
  could not be overridden, the derived one can. Decorative loops carry `.anim-loop` and look right at rest. Everything
  an animation conveys is also in text (status pills, timelines, the deliberation turns).
- Stylesheets: `wwwroot/css/tokens.css` (tokens, theme and motion switches), `base.css` (reset, typography, layout,
  focus), `components.css` (cards, pills, buttons, forms, tables, stepper, timeline, animation primitives) and
  `pages.css` (page-specific rules), linked in that order from `App.razor` through `@Assets` so each is fingerprinted;
  no `@import` or cross-file `url()`, which the asset pipeline does not rewrite. Scoped `.razor.css` files consume the
  tokens and never select on `data-theme`.
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

Configuration (`Ui` is this module's section):

```json
{ "Ui": { "DefaultTheme": "System", "ReducedMotion": false, "TransitionMilliseconds": 240 } }
```

Pages: none of its own; the design system is used by every page.

Known gaps: `light-dark()` needs a current browser (Baseline 2024); older browsers see the light palette.

Tests: `dotnet test` runs offline (options and validation, registration, the theme attributes, the refresh coalescer under a
fake clock: coalescing, the trailing read, the latency and its budget).
