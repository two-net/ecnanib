# Ecnanib — Software Requirements Specification

| | |
|---|---|
| Product | Ecnanib, a service for AI cryptocurrency trading agents |
| Version | 0.1, draft for review |
| Date | 11 November 2026 |
| Status | Draft. Items marked **To confirm** need a product-owner decision; they are collected in section 9. |

## Contents

1. Introduction
2. Overall description
3. System overview
4. Functional requirements
5. Non-functional requirements
6. Logical data model
7. Configuration file outline
8. Traceability to the brief
9. Risks and open questions

Appendix A. Default role mapping
Appendix B. Aggressive profile settings
References

---

## 1. Introduction

### 1.1 Purpose

This document states what Ecnanib must do and the constraints it must meet. It is the reference for design, implementation, testing, and acceptance.

### 1.2 Product summary

Ecnanib is a free, hosted web service. A user creates trading agents, and each agent:

- trades one symbol on Binance, on either the Spot or the Futures market;
- works on one timeframe: 15m, 1H, 4H, or 1D;
- is run by a team of four AI roles, filled by models from Claude, Gemini, GPT, and Grok that the user selects for that agent;
- uses the user's own API keys (BYOK), stored encrypted;
- trades with a single aggressive risk profile, inside hard limits enforced by rules that the AI cannot override.

### 1.3 Scope

**In scope for version 1**

- User accounts: registration, sign-in, preferences.
- BYOK credential storage for the four AI providers and Binance.
- Syncing a catalog of AI models from the four providers.
- Creating, configuring, running, and monitoring agents.
- Multi-provider collaboration with roles assigned by strength.
- Prompt sets that differ by timeframe and by market.
- Binance Spot and USDⓈ-margined perpetual Futures, in Live and Demo environments.
- Recording each AI provider's trading experience: track record and lessons.
- A web UI with dark, light, and system themes, and animation.
- Structured logging, PostgreSQL persistence, and single-file configuration.

**Out of scope for version 1**

- Exchanges other than Binance; coin-margined futures, delivery contracts, options, and spot margin.
- Withdrawals, deposits, or transfers of any kind.
- Billing, plans, subscriptions, and payments.
- Tenants, organizations, teams, shared agents, and copy trading.
- AI providers other than the four named.
- User-written prompts (see open question Q5).
- A backtesting engine, native mobile apps, and tax reporting.

### 1.4 Technology neutrality

This document does not choose a technology stack. It names no programming language, framework, UI library, ORM product, logging product, or hosting platform.

The only named technologies are the ones the brief fixes as constraints: PostgreSQL as the database, Binance as the exchange, and Claude, Gemini, GPT, and Grok as the AI providers. Where the brief says "like Serilog", "like appsettings", or "like ASPNETCORE_ENVIRONMENT", this document treats those as examples of a style and specifies the behavior, not the product.

### 1.5 Conventions

- Each requirement has an identifier (for example FR-AGT-3) and a priority.
- **Must**: required for version 1. **Should**: expected, but version 1 can ship without it. **Could**: desirable if time allows.
- All times are UTC unless stated otherwise.
- Default values are starting points and are set in the configuration file unless stated otherwise.

### 1.6 Definitions

| Term | Meaning |
|---|---|
| Agent | An automated trader that a user configures: one Binance market, one symbol, one timeframe, one team. |
| Team | The four AI roles that run an agent. |
| Role | Market Analyst, Sentiment Analyst, Strategist, or Risk Manager. |
| Provider | Claude (Anthropic), Gemini (Google), GPT (OpenAI), or Grok (xAI). |
| Model | A specific model offered by a provider, as listed in the catalog. |
| Catalog | The list of provider models that Ecnanib syncs and offers for selection. |
| BYOK | Bring your own key: the user supplies the API keys the service uses on their behalf. |
| Timeframe | The candle length an agent works on: 15m, 1H, 4H, or 1D. |
| Spot | Buying and selling the asset itself. |
| Futures | Binance USDⓈ-margined perpetual contracts, which allow long and short positions with leverage. |
| Cycle | One run of the team, triggered when a candle closes. |
| Risk Guard | Fixed, non-AI rules that check every plan and compute order size. |
| Agent equity | The capital the user allocated to an agent, plus the agent's net profit or loss to date. |
| R | The amount risked on a trade: the distance from entry to stop-loss, times quantity. Results are expressed as multiples of R. |
| Experience | A provider's or model's measured track record, plus lessons written after past trades. |
| Demo, Live | Demo uses Binance's test environment with no real funds. Live uses the user's real account. |
| Operator | A user with system-level status screens and safety controls. |

---

## 2. Overall description

### 2.1 Product perspective

Ecnanib is a new, standalone product, operated as software as a service: one shared deployment serves all users. It is free, with no subscription. There are no tenants; the only account type is the individual user.

It depends on three kinds of external systems: the four AI providers' APIs, Binance's APIs, and an email delivery service for account messages.

Ecnanib never holds user funds. Funds stay in the user's Binance account, and Ecnanib acts only through API keys the user supplies.

### 2.2 User classes

| Class | Description |
|---|---|
| Visitor | Not signed in. Can view public pages, register, and sign in. |
| User | A signed-in individual. Owns keys, agents, and their data. |
| Operator | A user listed as an operator in the configuration file. Has the status screens and safety controls in section 4.14. **To confirm.** |

### 2.3 Operating environment

- **Client:** the current and previous major versions of Chrome, Edge, Firefox, and Safari, at desktop and mobile widths.
- **Server:** one application instance and one PostgreSQL database, with fixed outbound IP addresses and a synchronized clock.

### 2.4 Constraints from the brief

| # | Constraint |
|---|---|
| C1 | The product is named Ecnanib. |
| C2 | Timeframes are 15m, 1H, 4H, and 1D, selectable per agent, each with its own prompt. |
| C3 | Agents trade aggressively. |
| C4 | The AI model catalog is synced from the providers. |
| C5 | The AI providers' trading experience is recorded and used. |
| C6 | The UI is animated. |
| C7 | The exchange is Binance, Spot and Futures, each with its own prompt. |
| C8 | Users bring their own keys, which are encrypted. |
| C9 | Claude, Gemini, GPT, and Grok work together, with roles assigned by strength, and the model is selected per provider at agent level. |
| C10 | Themes are dark, light, and system, with system as the default. |
| C11 | Logging uses a structured logging library in the style of Serilog. |
| C12 | The database is PostgreSQL, with automatic migrations, no partitioning, and a code-first ORM. |
| C13 | The architecture is simple. |
| C14 | Configuration is a single appsettings-style file, with no custom environment variables and no vault. |
| C15 | The product is free software as a service with users, no subscription, and no tenants. |

### 2.5 Assumptions and dependencies

**Assumptions** (each is a reading of the brief; see section 9 to confirm)

| # | Assumption |
|---|---|
| A1 | "Futures" means Binance USDⓈ-margined perpetual contracts. |
| A2 | One agent trades one symbol. |
| A3 | "AI providers trading experience" means a measured track record per provider and model, plus lessons from past trades that are fed back into prompts. |
| A4 | "Animation" means UI motion that communicates live state: the team at work, changing values, and transitions. |
| A5 | Prompt sets are supplied by the system. Users can read them but not edit them. |
| A6 | "Single config file" means one file with no per-environment variants. |
| A7 | An Operator role exists for safety controls. |
| A8 | The UI language is English. |

**Dependencies**

| # | Dependency |
|---|---|
| D1 | Each AI provider offers an interface that lists its models. The metadata it returns varies by provider. |
| D2 | Binance offers demo or test environments for Spot and Futures. |
| D3 | Binance counts request limits per IP address, so all users of the service share one budget. |
| D4 | Users pay AI providers and Binance directly through their own accounts. Ecnanib carries no AI or exchange cost for trading. |

---

## 3. System overview

### 3.1 Logical components

| Component | Responsibility |
|---|---|
| Web UI | Screens, themes, animation, live updates. |
| Accounts | Registration, sign-in, preferences, audit trail. |
| Credentials | BYOK storage, encryption, validation. |
| Catalog | Model sync and eligibility. |
| Agents | Agent settings and lifecycle. |
| Decision Engine | Schedules cycles, composes prompts, runs the team, validates outputs. |
| Risk Guard | Non-AI limit checks and position sizing. |
| AI Provider Gateway | One adapter per provider behind a common interface. |
| Exchange Gateway | Binance market data, orders, account events, reconciliation, rate limiting. |
| Experience | Track record, strength scores, lessons. |
| Notifications | In-app and email messages. |

All components run inside one application with one database (section 5.1).

### 3.2 Decision cycle

1. A candle closes for the agent's timeframe.
2. The system builds the data package: closed candles, indicators, higher-timeframe context, current position, and limits. Futures adds mark price, funding, and open interest.
3. The Market Analyst and the Sentiment Analyst run in parallel and each return a structured report.
4. The Strategist receives both reports and recalled lessons, and returns a trade plan.
5. The Risk Manager reviews the plan and approves, reduces, or vetoes it.
6. The Risk Guard applies the hard limits and computes the order size.
7. The Exchange Gateway places the entry and the protective orders on Binance, or does nothing.
8. The cycle is recorded and the UI updates live. When a position closes, experience is updated and a lesson is written.

### 3.3 Agent states

| State | Entered when | Cycles run | New entries | Open position |
|---|---|---|---|---|
| Draft | The agent is created and not yet started. | No | No | None |
| Running | The user starts or resumes the agent. | Yes | Yes | Managed by the team |
| Paused | The user pauses the agent. | No | No | Stays open with its protective orders |
| Halted | A Risk Guard loss limit is reached (FR-RSK-8). | No | No | Closed by the system when the limit is reached |
| Attention required | A key is invalid, a model is unavailable, an exchange setting conflicts, or calls fail repeatedly. | No | No | Stays open with its protective orders |
| Stopped | The user stops the agent. | No | No | Closed or kept, as the user chose |

---

## 4. Functional requirements

### 4.1 Accounts and access

| ID | Requirement | Priority |
|---|---|---|
| FR-ACC-1 | A visitor can register with an email address and password, and must verify the email address before creating agents. | Must |
| FR-ACC-2 | A user can sign in, sign out, and reset a forgotten password by email. | Must |
| FR-ACC-3 | A user can enable two-factor authentication with an authenticator app. | Should |
| FR-ACC-4 | Sensitive actions require re-authentication: adding or replacing keys, switching an agent to Live, and deleting the account. | Must |
| FR-ACC-5 | The only account type is the individual user. There are no tenants, organizations, workspaces, teams, or shared resources. Every resource belongs to exactly one user. | Must |
| FR-ACC-6 | The service is free. There are no plans, subscriptions, payments, invoices, trials, or paid tiers, and no behavior depends on them. | Must |
| FR-ACC-7 | Fair-use limits (for example, the maximum number of agents per user) and whether registration is open are set in the configuration file and apply equally to all users. | Must |
| FR-ACC-8 | At registration the user accepts the Terms of Service, the Privacy Policy, and the risk disclosure. The accepted versions and the time are recorded. | Must |
| FR-ACC-9 | A user can delete their account. Deletion requires all agents to be stopped, purges stored credentials immediately, and removes personal data. Anonymized aggregate statistics may be kept. | Must |

### 4.2 Bring your own key

| ID | Requirement | Priority |
|---|---|---|
| FR-KEY-1 | Users supply their own API keys for each AI provider (Claude, Gemini, GPT, Grok) and for Binance. Ecnanib provides no shared keys for trading decisions or orders. | Must |
| FR-KEY-2 | A user can store one key per AI provider and one Binance credential per environment (Live, Demo). | Must |
| FR-KEY-3 | When a key is saved, the system validates it with a read-only call and records its status, detected permissions, and validation time. Keys are validated again daily and whenever a call fails authentication. | Must |
| FR-KEY-4 | A Binance key that the exchange reports as able to withdraw is rejected. Where the exchange reports it, the system warns when a key has no IP restriction, and shows the service's fixed outbound IP addresses so the user can allow-list them. | Must |
| FR-KEY-5 | A Binance key that lacks the permission an agent's market needs (Spot trading or Futures trading) blocks that agent from starting, with a message naming the missing permission. | Must |
| FR-KEY-6 | All stored keys and secrets are encrypted at rest with authenticated encryption, a 256-bit key, and a unique nonce per value. Each stored value records which master key encrypted it. | Must |
| FR-KEY-7 | Master keys are read from the configuration file. Several may be listed with one marked active, so keys can be rotated, and a re-encryption routine moves all stored values to the active key. | Must |
| FR-KEY-8 | Secrets are write-only. After saving, only a masked form (the last four characters) is shown. Secrets are never sent to the browser, logged, placed in prompts, or included in exports or error messages. | Must |
| FR-KEY-9 | Secrets are decrypted only in memory and only for the call that needs them. | Must |
| FR-KEY-10 | A user can replace or delete any key at any time. Deletion is permanent. Agents that depend on a missing or invalid key move to Attention required. | Must |
| FR-KEY-11 | The system shows usage for each of the user's AI keys: calls, tokens, and estimated cost where pricing is known. | Should |

### 4.3 AI model catalog syncing

| ID | Requirement | Priority |
|---|---|---|
| FR-CAT-1 | The system keeps a catalog of models for Claude, Gemini, GPT, and Grok. The catalog is filled only by syncing from each provider's official model-listing interface. No model list is hard-coded. | Must |
| FR-CAT-2 | Sync runs on a schedule (default every 24 hours), at startup when the catalog is empty, and on demand by an Operator. | Must |
| FR-CAT-3 | Sync authenticates with a catalog credential for each provider from the configuration file. If none is configured for a provider, that provider's catalog is refreshed whenever any user's key for it is validated. | Must |
| FR-CAT-4 | Each model is stored once per provider and model identifier, with its display name, any metadata the provider exposes (for example context size, output limit, release date, deprecation date), first-seen time, and last-seen time. Missing metadata is stored as unknown, not guessed. | Must |
| FR-CAT-5 | A model that is absent from a set number of consecutive syncs (default 2) is marked Unavailable. Models are never deleted, so history and experience records stay valid. A model that reappears becomes Active again. | Must |
| FR-CAT-6 | Only eligible models can be selected for agents. Eligibility rules (allow and deny patterns per provider, and capability flags such as structured output and live search) are set in the configuration file, because providers do not report capabilities uniformly. | Must |
| FR-CAT-7 | A failed sync for one provider does not affect the others and leaves that provider's existing catalog unchanged. Each sync run records its time, its result, and counts of added, changed, and unavailable models. | Must |
| FR-CAT-8 | When a model assigned to an agent becomes Unavailable, or a call reports that the model does not exist, the agent moves to Attention required and the user is notified. The system does not substitute another model on its own. | Must |
| FR-CAT-9 | When a user's AI key is validated, the system records which catalog models that key can use, and the model selector offers only those. | Should |
| FR-CAT-10 | A catalog screen lists models by provider with status, metadata, last sync time, and a link to each model's experience record. | Should |
| FR-CAT-11 | Price per token can be supplied in the configuration file for models whose provider does not report pricing, so cost can be estimated. | Could |

### 4.4 Agents

| ID | Requirement | Priority |
|---|---|---|
| FR-AGT-1 | A user can create, edit, start, pause, resume, stop, clone, and delete agents. Agent names are unique per user. | Must |
| FR-AGT-2 | Agent settings are: name; mode (Demo or Live); market (Spot or Futures); symbol; timeframe; team (section 4.5); allocated capital in the quote currency; and risk settings within the bounds in Appendix B. | Must |
| FR-AGT-3 | Timeframe is selected per agent from 15m, 1H, 4H, and 1D. It sets the cycle schedule, the data window, and the prompt variant. | Must |
| FR-AGT-4 | Market is selected per agent from Spot and Futures. It sets the exchange interface, the allowed actions, and the prompt variant. | Must |
| FR-AGT-5 | For each role in the team, the user selects the provider and the specific model from the catalog. Model choice is stored per agent, so two agents can use different models from the same provider. | Must |
| FR-AGT-6 | The symbol is chosen from a list in the configuration file, filtered to symbols that exist on the selected market. | Must |
| FR-AGT-7 | New agents start in Demo mode. Switching to Live requires a valid Live Binance key, an explicit risk confirmation, and re-authentication, and is recorded in the audit trail. | Must |
| FR-AGT-8 | Mode, market, symbol, and timeframe can be changed only while the agent is in Draft, or is Stopped with no open position or orders. Team and risk changes apply from the next cycle. | Must |
| FR-AGT-9 | A user can have at most one active agent (any state other than Draft or Stopped) for each combination of Binance credential, market, and symbol. | Must |
| FR-AGT-10 | Agents follow the states in section 3.3. Every state change records its cause and time and is shown to the user. | Must |
| FR-AGT-11 | When stopping an agent that has an open position, the user chooses to close the position at market or to keep it with its protective orders. | Must |
| FR-AGT-12 | A "Close position now" control is available at all times for any agent with an open position. | Must |

### 4.5 Team: four providers working together

| Role | Job | Structured result |
|---|---|---|
| Market Analyst | Reads price, volume, and indicators, and on Futures also funding and open interest. | Bias (bullish, bearish, or neutral), key price levels, volatility read. |
| Sentiment Analyst | Reads news and social context for the symbol and the wider market. | Bias, notable events, confidence, and whether live data was available. |
| Strategist | Combines both reports, recalled lessons, and the current position into a trade plan. | Action, entry type and price, stop-loss, take-profit, risk fraction, rationale. |
| Risk Manager | Reviews the plan independently. | Approve, reduce, or veto, with reasons. |

| ID | Requirement | Priority |
|---|---|---|
| FR-TEAM-1 | Every agent has the four roles above. | Must |
| FR-TEAM-2 | By default the four roles are filled by four different providers, so that Claude, Gemini, GPT, and Grok work together in each agent. | Must |
| FR-TEAM-3 | The default role-to-provider mapping is read from the configuration file. The initial mapping and its rationale are in Appendix A. | Must |
| FR-TEAM-4 | Role by strength: the system computes a strength score for each model in each role from measured experience (FR-EXP-4), shows the strongest candidate for each role, and lets the user apply the recommended team in one action or override any role. | Must |
| FR-TEAM-5 | A user who lacks a key for a provider can assign another provider to that role, so one provider may hold more than one role. The UI warns that the team is reduced, and warns specifically when the Strategist and the Risk Manager use the same provider. **To confirm.** | Should |
| FR-TEAM-6 | Roles run in the order in section 3.2. Each role receives only the inputs defined for it and returns a structured result that is validated against a fixed schema, plus a short rationale. | Must |
| FR-TEAM-7 | The Risk Manager may approve a plan, reduce its risk fraction, or veto it. It cannot increase risk, change direction, or widen the stop-loss, and it cannot block a plan that closes a position or tightens protection. | Must |
| FR-TEAM-8 | Each role call records the provider, model, prompt template version, latency, token usage, structured result, and validation outcome. | Must |
| FR-TEAM-9 | If a role call times out or returns an invalid result, it is retried once. If the Sentiment Analyst still fails, the cycle continues with sentiment marked unavailable. If any other role still fails, the cycle ends with no decision and no new entry. | Must |
| FR-TEAM-10 | An agent can opt in to automatic role reassignment by strength, applied at most once every 7 days and only when the agent has no open position. Each change is logged and notified. | Could |

### 4.6 Prompts

| ID | Requirement | Priority |
|---|---|---|
| FR-PRM-1 | The prompt for each role call is composed from: role instructions, a market module, a timeframe module, the aggressive profile module, agent context (symbol, equity, position, limits), the data package, and recalled lessons. | Must |
| FR-PRM-2 | There is a distinct timeframe module for each of 15m, 1H, 4H, and 1D, as outlined in the first table below. | Must |
| FR-PRM-3 | There is a distinct market module for Spot and for Futures, as outlined in the second table below. | Must |
| FR-PRM-4 | The eight combinations of market and timeframe are selected automatically from the agent's settings. A mismatched combination cannot be chosen. | Must |
| FR-PRM-5 | The aggressive profile module instructs the team to look for opportunities actively, act on moderate-conviction setups, use the full risk allowed, and trade both directions on Futures, while always defining a stop-loss and staying within the stated limits. | Must |
| FR-PRM-6 | Prompt templates are versioned. A version that has been used is never changed; an edit creates a new version. Each role output records the template versions it used. | Must |
| FR-PRM-7 | Templates are managed by the system and delivered as seed data. Users can read the templates their agents use but cannot edit them. | Must |
| FR-PRM-8 | Templates instruct models to treat news, social posts, and web content as untrusted data, to ignore instructions found inside such content, and to return only the defined schema. | Must |
| FR-PRM-9 | Prompts never contain secrets, email addresses, account identifiers, or other personal data. | Must |
| FR-PRM-10 | Template text is provider-neutral. Provider-specific handling is limited to how a structured result is requested. | Should |

**Timeframe modules**

| Timeframe | Style the module sets | Context supplied | Expected holding period |
|---|---|---|---|
| 15m | Intraday momentum and pullbacks, with close attention to fees and spread. | 15m candles, with 1H and 4H context. | Minutes to hours |
| 1H | Intraday to short swing. | 1H candles, with 4H and 1D context. | Hours to days |
| 4H | Swing. | 4H candles, with 1D and 1W context. | Days to weeks |
| 1D | Position. | 1D candles, with 1W context. | Weeks to months |

**Market modules**

| Market | What the module sets |
|---|---|
| Spot | Long-only. Actions are open long, hold, close, and adjust protection. No leverage and no shorting; a bearish view means staying in the quote currency. |
| Futures | Long and short. Adds open short and reverse. Covers leverage within the cap, isolated margin, liquidation distance, funding cost over the expected holding period, and reduce-only exits. |

### 4.7 Aggressive profile and Risk Guard

| ID | Requirement | Priority |
|---|---|---|
| FR-RSK-1 | The product has one trading profile, Aggressive. Its defaults and hard bounds are in Appendix B and are read from the configuration file. | Must |
| FR-RSK-2 | A user can adjust an agent's risk settings only within the hard bounds. | Must |
| FR-RSK-3 | The Risk Guard is a set of fixed rules applied after the Risk Manager and before any order. Neither AI output nor a user action can bypass it for a trade. | Must |
| FR-RSK-4 | The Risk Guard rejects a plan to open a position unless all of these hold: a stop-loss is present and on the correct side of entry; the reward-to-risk ratio meets the minimum; the entry price is within the allowed distance of the current price; the order satisfies the exchange's rules for price step, quantity step, and minimum value; the daily loss limit and drawdown limit are not reached; the decision is not stale (FR-CYC-9); the agent's mode, market, and symbol are permitted; and no global halt is active. A plan that only closes a position or adjusts protection is checked against the exchange's rules and FR-CYC-6 only, so that risk can always be reduced. | Must |
| FR-RSK-5 | Models do not choose quantity or leverage. The system computes quantity from the approved risk fraction, the agent's equity, and the stop distance. On Spot, position value is capped at the agent's equity. On Futures, the system uses the lowest leverage setting that fits the position's margin within the agent's equity, up to the leverage cap, and reduces quantity if the cap would be exceeded. | Must |
| FR-RSK-6 | On Futures, the estimated liquidation price must be further from entry than the stop-loss by at least the liquidation buffer factor, or the plan is rejected. | Must |
| FR-RSK-7 | Every open position has a stop-loss order resting on the exchange. If the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user. | Must |
| FR-RSK-8 | When an agent's loss for the UTC day reaches the daily loss limit, the system closes its position and halts it until the next UTC day. When its drawdown from peak equity reaches the drawdown limit, the system closes its position and halts it until the user resumes it. Both limits include unrealized profit and loss and are evaluated at every cycle and every reconciliation. | Must |
| FR-RSK-9 | Each Risk Guard check stores the outcome of every rule, so a user can see exactly why a plan was accepted or rejected. | Must |

### 4.8 Binance integration

**Both markets**

| ID | Requirement | Priority |
|---|---|---|
| FR-BIN-1 | Binance is the only exchange. Integration uses Binance's official public APIs for Spot and for USDⓈ-margined perpetual Futures. | Must |
| FR-BIN-2 | Each market is supported in two environments: Live, and Binance's official demo or test environment. Endpoints for all four are set in the configuration file. | Must |
| FR-BIN-3 | Market data (candles, prices, and symbol rules; on Futures also mark price, funding rate, and open interest) is fetched once per symbol and timeframe and shared by all agents that need it. | Must |
| FR-BIN-4 | A single service-wide rate limiter governs all Binance requests, because Binance counts limits per IP address. It keeps usage under a configured share of the limit, backs off when the exchange signals throttling, never retries in a tight loop, and shares capacity fairly among users. | Must |
| FR-BIN-5 | The server clock stays within the tolerance Binance requires for signed requests. Drift is monitored, and new entries pause if drift exceeds the tolerance. | Must |
| FR-BIN-6 | Every order carries a unique client order identifier. If the outcome of a request is unknown, the system queries the order's status before any retry, so an order is never sent twice. | Must |
| FR-BIN-7 | Order and position changes are tracked through the exchange's account event feed where available, and by reconciliation at startup and every 5 minutes. On any mismatch the exchange is treated as the source of truth, records are corrected, and the mismatch is logged and shown. | Must |
| FR-BIN-8 | Actions the user takes directly on Binance (for example closing a position by hand) are detected by reconciliation and reflected in the agent's records. | Must |
| FR-BIN-9 | Ecnanib never withdraws, deposits, or transfers funds, including between the user's Spot and Futures wallets. | Must |
| FR-BIN-10 | Fees and funding payments are taken from exchange data and included in every profit and loss figure. | Must |
| FR-BIN-11 | Demo results are labeled as Demo everywhere and are never combined with Live results. | Must |

**Spot**

| ID | Requirement | Priority |
|---|---|---|
| FR-BIN-S1 | Spot agents are long-only: they buy the base asset with the quote asset and sell to exit. They do not borrow or short. | Must |
| FR-BIN-S2 | Entries are market or limit orders. An unfilled limit entry is cancelled at the next cycle. Protection is a stop-loss and take-profit pair resting on the exchange, where filling one cancels the other. | Must |
| FR-BIN-S3 | A Spot agent sells only the quantity it bought and never touches other holdings in the account. If the quote balance is below what the plan needs, the entry is skipped and the user is notified. | Must |

**Futures**

| ID | Requirement | Priority |
|---|---|---|
| FR-BIN-F1 | Futures agents trade USDⓈ-margined perpetual contracts and can be long or short. | Must |
| FR-BIN-F2 | Futures agents use isolated margin and one-way position mode. The system sets margin type and leverage for the agent's symbol before entry. It does not change account-wide settings; if the account is in a conflicting mode or already holds a position in the symbol, the agent does not start and the user is told what to change. | Must |
| FR-BIN-F3 | Protection is a reduce-only stop-loss and a reduce-only take-profit resting on the exchange, triggered on the mark price. | Must |
| FR-BIN-F4 | The current funding rate and the next funding time are included in the data package, and funding paid or received is recorded per position. | Must |
| FR-BIN-F5 | The system checks the margin state of open positions at each reconciliation and alerts the user when a position approaches liquidation. It never adds margin on its own. | Must |
| FR-BIN-F6 | An entry is skipped, and the user notified, when the Futures wallet lacks the margin the plan needs. | Must |

### 4.9 Decision cycle

| ID | Requirement | Priority |
|---|---|---|
| FR-CYC-1 | A cycle starts when a candle of the agent's timeframe closes, on UTC boundaries. Only closed candles are used. | Must |
| FR-CYC-2 | The data package contains: the most recent closed candles for the agent's timeframe (default 200); higher-timeframe context as in section 4.6; indicators computed by the system, not by models (moving averages, relative strength, average true range, volume averages); the current position, orders, and equity; the agent's limits; and on Futures the mark price, funding, and open interest. | Must |
| FR-CYC-3 | The Sentiment Analyst gathers news and social context using the live-search capability of its model where the provider offers one. If the selected model has none, the role still runs, its report is marked "no live data", and the Strategist is told to give it no weight. The model selector shows which models have live search and discloses that it may add provider charges. | Must |
| FR-CYC-4 | Allowed actions on Spot are open long, hold, close, and adjust protection. Futures adds open short and reverse (close, then open the opposite side). | Must |
| FR-CYC-5 | An agent holds at most one position. Adding to an open position is not supported. | Must |
| FR-CYC-6 | Adjust protection may move the stop-loss only in the direction that reduces risk. The take-profit may move in either direction. | Must |
| FR-CYC-7 | A plan to open a position always includes a stop-loss price and a take-profit price. | Must |
| FR-CYC-8 | Cycles missed while the service was down are skipped, not replayed. After a restart the system reconciles first, then waits for the next candle close. | Must |
| FR-CYC-9 | A decision that completes later than the stale limit for its timeframe (Appendix B) cannot open a position. It can still close a position or tighten protection. | Must |
| FR-CYC-10 | Each cycle is stored as a complete record: data package summary, every role output, Risk Guard results, orders, and final outcome. The user can view it as a timeline. | Must |
| FR-CYC-11 | When many agents share a candle boundary, cycles are queued with concurrency limits per provider and per user key, shortest timeframe first. | Must |
| FR-CYC-12 | A user can run a test cycle for an agent that produces a full decision record and places no orders. | Should |

### 4.10 AI providers' trading experience

| ID | Requirement | Priority |
|---|---|---|
| FR-EXP-1 | For every closed position the system records net profit or loss, the result in R, fees, funding, holding time, and exit reason, linked to the agent, market, timeframe, symbol, mode, and the model that held each role. | Must |
| FR-EXP-2 | The system maintains a track record per provider and per model, filterable by role, market, timeframe, mode, and period, showing: closed trades, win rate, average R, total R, profit factor, largest drawdown in R, and average holding time. | Must |
| FR-EXP-3 | A role-specific measure is kept for each role. For the two analyst roles: the share of non-neutral calls whose direction matched the price change over the next 4 closed candles, minus 50%. For the Strategist: the average R of executed plans. For the Risk Manager: the average R of approved plans minus the average R that vetoed plans would have produced. | Must |
| FR-EXP-4 | A strength score per model, role, market, and timeframe is the role's measure damped toward zero for small samples: measure × n ÷ (n + 20), where n is the sample size. Slices with fewer than 20 samples show "not enough data" and are excluded from recommendations. The formula is shown in the UI. | Must |
| FR-EXP-5 | Vetoed plans are evaluated as shadow trades against later closed candles, using their own entry, stop-loss, and take-profit with standard fees, and assuming the stop-loss was hit first when both fall inside one candle. Shadow trades never place orders. | Must |
| FR-EXP-6 | After each closed position, the agent's Risk Manager model writes a short lesson: the setup, what was expected, what happened, and one takeaway of at most 60 words. | Must |
| FR-EXP-7 | Each Strategist and Risk Manager prompt includes up to 5 recent lessons, taken first from the same agent and then from the user's other agents on the same symbol, market, and timeframe, within a fixed size budget. | Must |
| FR-EXP-8 | A user can read all of their lessons, disable or delete any lesson, and reset an agent's lessons. | Must |
| FR-EXP-9 | Demo and Live experience are kept and scored separately. Recommendations use Live data where there is enough, otherwise Demo data, and say which was used. | Must |
| FR-EXP-10 | A platform-wide board shows the same track record aggregated across all users. A slice is shown only when at least 5 users and 50 trades contribute. It exposes no user identity, balance, or individual trade, and users can opt out of contributing. **To confirm.** | Should |
| FR-EXP-11 | Experience screens state that past results do not predict future results, that small samples are unreliable, and that Demo results differ from Live. | Must |

### 4.11 Dashboard and reporting

| ID | Requirement | Priority |
|---|---|---|
| FR-RPT-1 | The dashboard shows, per mode: total equity across agents, profit and loss for the day, open positions, a status card per agent, recent decisions, and alerts. | Must |
| FR-RPT-2 | The agent screen shows: live team status, the current position and protective orders, an equity curve, the trade list, performance figures (return, win rate, average R, profit factor, largest drawdown, fees, funding), and the decision timeline with every role's output and the Risk Guard result. | Must |
| FR-RPT-3 | AI usage is shown per agent, provider, and day: calls, tokens, and estimated cost where pricing is known. | Must |
| FR-RPT-4 | A user can export their trades and decisions as CSV. | Should |

### 4.12 Notifications and live updates

| ID | Requirement | Priority |
|---|---|---|
| FR-NTF-1 | In-app notifications cover: position opened or closed; agent halted or needing attention; key invalid; model unavailable; order or protection failure; reconciliation mismatch; and liquidation warning. | Must |
| FR-NTF-2 | Agent status, positions, prices, and cycle progress update in the UI within 2 seconds, without a manual refresh. | Must |
| FR-NTF-3 | Users can opt in to email for critical events. | Should |

### 4.13 User interface

**Theme**

| ID | Requirement | Priority |
|---|---|---|
| FR-THM-1 | The user can choose Dark, Light, or System. System is the default for new users and for visitors. | Must |
| FR-THM-2 | System follows the device's color-scheme preference and switches live when that preference changes. | Must |
| FR-THM-3 | The choice is saved to the user's account and applied on every device they sign in on. For visitors it is saved on the device. | Must |
| FR-THM-4 | The correct theme is applied before the first paint, with no flash of the other theme. | Must |
| FR-THM-5 | Every screen, chart, and animation works in both themes, with text contrast meeting WCAG 2.1 AA. Profit and loss are distinguishable by sign or icon as well as by color. | Must |

**Animation**

| ID | Requirement | Priority |
|---|---|---|
| FR-ANI-1 | The agent screen shows the team at work as an animated pipeline: each role appears with its provider and model, moves through waiting, working, done, or failed, and the hand-off between roles is visible as the cycle progresses. | Must |
| FR-ANI-2 | Changing figures (price, profit and loss, equity) animate from the old value to the new one, and charts animate when they load and when new data arrives. | Must |
| FR-ANI-3 | Agent state changes, navigation, dialogs, and list changes use short transitions. Content areas show skeleton placeholders while loading. | Must |
| FR-ANI-4 | When the device asks for reduced motion, non-essential animation is replaced by instant changes or simple fades. The user can also reduce animation in settings. | Must |
| FR-ANI-5 | Animation never blocks input. Interface transitions last 150 to 400 ms. Continuous animation is used only for states that are live. Nothing flashes more than three times per second. | Must |
| FR-ANI-6 | Animations hold 60 frames per second on mid-range devices, cause no layout shift, and pause when off screen or in a background tab. | Must |
| FR-ANI-7 | The public landing page presents the four-provider team with an animated illustration. | Could |

**Layout**

| ID | Requirement | Priority |
|---|---|---|
| FR-UI-1 | The UI is a responsive web application, usable at desktop and mobile widths. | Must |
| FR-UI-2 | Screens: landing, registration and sign-in, dashboard, agent list, agent editor, agent detail, experience, model catalog, settings (profile, security, API keys, appearance, notifications), and operator status. | Must |
| FR-UI-3 | A Demo or Live badge is always visible on agent screens. Every action that affects Live trading asks for confirmation. | Must |
| FR-UI-4 | First-time users see a setup checklist: add keys, create a Demo agent, review its decisions, then consider Live. | Should |

### 4.14 Operator functions

| ID | Requirement | Priority |
|---|---|---|
| FR-OPS-1 | Operators are the users whose email addresses are listed in the configuration file. | Must |
| FR-OPS-2 | An operator status screen shows scheduler health, queue depth, last catalog sync per provider, Binance rate-limit usage, clock drift, table sizes, and error rates. | Should |
| FR-OPS-3 | An Operator can switch a global halt on or off. While it is on, no agent opens a new position; open positions are still managed and protected. | Must |
| FR-OPS-4 | An Operator can disable or enable a user account and trigger a catalog sync. | Should |
| FR-OPS-5 | Operator screens never show user secrets. All operator actions are recorded in the audit trail. | Must |

---

## 5. Non-functional requirements

### 5.1 Architecture

| ID | Requirement | Priority |
|---|---|---|
| NFR-ARC-1 | The system is one deployable application that serves the UI, exposes the API, and runs the scheduler and background work, with one PostgreSQL database. | Must |
| NFR-ARC-2 | There are no microservices, message brokers, separate cache servers, or search servers. | Must |
| NFR-ARC-3 | Code is organized into the components in section 3.1 with one-directional dependencies. Each external system (each AI provider, Binance, email) sits behind an adapter with a common interface, so adding a provider does not change the Decision Engine. | Must |
| NFR-ARC-4 | Background work keeps its state in the database so that it survives a restart. Version 1 targets a single running instance; anything that must not run twice (migrations, schedulers) takes a database lock, so a second instance would be safe. | Must |
| NFR-ARC-5 | The system requires no proprietary cloud service and runs anywhere the application and PostgreSQL can run. | Must |
| NFR-ARC-6 | Live UI updates use one push channel from the application to the browser. | Should |

### 5.2 Configuration

| ID | Requirement | Priority |
|---|---|---|
| NFR-CFG-1 | One hierarchical configuration file, in the style of an appsettings file, holds every application setting, including secrets: the database connection, master encryption keys, email credentials, and optional catalog credentials. | Must |
| NFR-CFG-2 | The application does not read its settings from environment variables, command-line arguments, per-environment override files, or any vault or external secret store. | Must |
| NFR-CFG-3 | Environment variables that the runtime platform defines by default (for example its environment-name selector, such as ASPNETCORE_ENVIRONMENT) keep their default behavior. The application neither removes nor repurposes them, and adds none of its own. | Must |
| NFR-CFG-4 | The file is validated at startup for required keys, types, ranges, and key lengths. On any error the application refuses to start and reports which keys are wrong, without printing secret values. | Must |
| NFR-CFG-5 | A template of the file with placeholders is kept in source control. The real file is excluded from source control and is readable only by the account that runs the application. | Must |
| NFR-CFG-6 | User preferences, agent settings, and runtime flags such as the global halt are stored in the database, not in the file. | Must |
| NFR-CFG-7 | Log levels can change without a restart when the file changes. Other changes take effect at restart. | Should |

### 5.3 Data and persistence

| ID | Requirement | Priority |
|---|---|---|
| NFR-DAT-1 | PostgreSQL is the only data store. | Must |
| NFR-DAT-2 | The schema is defined code-first through an object-relational mapper. The code model is the source of truth, and migrations are generated from it and versioned with the code. | Must |
| NFR-DAT-3 | Pending migrations are applied automatically at startup, before the application serves requests or starts schedulers, under a database lock. If a migration fails, the application logs the error and does not start. | Must |
| NFR-DAT-4 | No table is partitioned, by any mechanism. | Must |
| NFR-DAT-5 | Table growth is controlled by indexes and by scheduled retention jobs that delete in small batches. Default retention: role transcripts 90 days; decision records 365 days; notifications 90 days; audit events 730 days. Orders, fills, positions, and lessons are kept indefinitely. Equity snapshots are thinned to one per day after 90 days. | Must |
| NFR-DAT-6 | Candles are cached only for as long as cycles need them. The database is not a market-data warehouse. | Must |
| NFR-DAT-7 | Operational logs are not written to the database. | Must |
| NFR-DAT-8 | Prices, quantities, and money are stored as exact decimals. Timestamps are stored in UTC. | Must |
| NFR-DAT-9 | Every user-owned row carries its owner, and every query for user data is scoped to the signed-in user. | Must |
| NFR-DAT-10 | Roles, providers, and prompt templates are seeded through migrations. | Must |
| NFR-DAT-11 | The database is backed up daily and restores are tested. A migration that drops or rewrites data is marked as destructive and is preceded by a backup. | Must |

### 5.4 Logging and audit

| ID | Requirement | Priority |
|---|---|---|
| NFR-LOG-1 | The application uses a structured logging library in the style of Serilog: events are written from message templates with named properties, carry a level, can be enriched with context, and are sent to configurable outputs. | Must |
| NFR-LOG-2 | Logging is configured only in the configuration file: minimum level, per-area overrides, and outputs. Console output and a daily rolling file with a retention limit are available at minimum. | Must |
| NFR-LOG-3 | Events inside a request or a cycle carry correlation properties: request id, user id, agent id, cycle id, role, provider, model, and exchange order id where relevant. | Must |
| NFR-LOG-4 | Secrets, API keys, tokens, and passwords never appear in logs. Masking is enforced in one central place and covered by an automated test. | Must |
| NFR-LOG-5 | Every outbound call to an AI provider or to Binance is logged with its duration, outcome, and usage or request weight. Request and response bodies are logged only at debug level, which is off by default in production. | Must |
| NFR-LOG-6 | Unhandled errors are logged with full detail and correlation properties. The user sees a plain message with a reference id. | Must |
| NFR-LOG-7 | Separately from logs, an audit trail in the database records security- and money-relevant actions: sign-ins, two-factor changes, key changes, agent start, pause, and stop, mode switches, manual closes, risk setting changes, and operator actions. | Must |

### 5.5 Security and privacy

| ID | Requirement | Priority |
|---|---|---|
| NFR-SEC-1 | All traffic uses TLS. Session cookies are secure and inaccessible to scripts. Sessions expire after inactivity. | Must |
| NFR-SEC-2 | Passwords are stored only as salted hashes from a memory-hard algorithm. | Must |
| NFR-SEC-3 | Every request for a resource checks that the resource belongs to the signed-in user. Operator functions are checked separately. | Must |
| NFR-SEC-4 | The application defends against injection, cross-site scripting, request forgery, and clickjacking, and rate-limits sign-in, registration, and password reset. | Must |
| NFR-SEC-5 | Stored secrets are encrypted as required by FR-KEY-6 to FR-KEY-9. | Must |
| NFR-SEC-6 | Models cannot place orders or call internal functions. The only path from a model's output to an order is schema validation, then the Risk Guard, then the Exchange Gateway. | Must |
| NFR-SEC-7 | Content from news, social posts, and web search is treated as untrusted. Results outside the schema, or with values outside the allowed ranges, are rejected. | Must |
| NFR-SEC-8 | Data sent to AI providers is limited to market data, agent settings, position state, and lessons. Users are told that this data goes to the providers they configure, under their own keys and those providers' terms. | Must |
| NFR-SEC-9 | Personal data is limited to email address, password hash, and preferences. Users can export and delete their data. | Must |
| NFR-SEC-10 | Dependencies are scanned for known vulnerabilities as part of delivery. | Should |

### 5.6 Performance and capacity

| ID | Requirement | Priority |
|---|---|---|
| NFR-PRF-1 | Typical screens respond within 300 ms at the 95th percentile, measured at the server. | Should |
| NFR-PRF-2 | With providers responding normally, 95% of cycles finish within 90 seconds of candle close. | Must |
| NFR-PRF-3 | A single instance supports at least 500 running agents, with every cycle at a shared candle boundary started within 30 seconds. **To confirm by load test.** | Should |
| NFR-PRF-4 | A catalog sync for one provider finishes within 60 seconds. | Should |

### 5.7 Reliability and recovery

| ID | Requirement | Priority |
|---|---|---|
| NFR-REL-1 | Target availability is 99.5% per month, excluding announced maintenance. | Should |
| NFR-REL-2 | Failures are safe: any internal error, timeout, or dependency outage results in no new entry. Open positions stay protected by orders resting on the exchange, which do not depend on Ecnanib being up. | Must |
| NFR-REL-3 | On startup the application migrates the database, reconciles every running agent with the exchange, then resumes scheduling. A restart never creates a duplicate order. | Must |
| NFR-REL-4 | An outage at one AI provider affects only the cycles that need it. Repeated failures for a provider or a key trip a breaker that pauses calls and retries later. A Binance outage pauses entries and raises an alert. | Must |
| NFR-REL-5 | Shutdown is graceful: in-flight cycles finish or abort safely, and no entry is left without protection. | Must |
| NFR-REL-6 | Maintenance never closes positions. A banner announces it in advance. | Must |

### 5.8 Usability and accessibility

| ID | Requirement | Priority |
|---|---|---|
| NFR-USA-1 | The UI meets WCAG 2.1 AA, is fully operable by keyboard, and labels controls for screen readers. | Must |
| NFR-USA-2 | Risk settings, agent states, and Risk Guard rejections are explained in plain language where they appear. | Must |
| NFR-USA-3 | Times are shown in the user's local time zone, with UTC available. | Must |
| NFR-USA-4 | The UI is in English. Text is kept separate from code so that other languages can be added. | Should |

### 5.9 Legal and compliance

| ID | Requirement | Priority |
|---|---|---|
| NFR-LEG-1 | The risk disclosure states that automated trading can lose all allocated capital, that the aggressive profile raises that risk, that leverage can cause rapid losses, that nothing in the service is investment advice, and that past results do not predict future results. | Must |
| NFR-LEG-2 | Ecnanib never takes custody of funds and refuses keys that can withdraw. | Must |
| NFR-LEG-3 | Before public launch, qualified counsel reviews whether operating an automated trading service requires licensing or registration in the target jurisdictions, and any required restrictions by country are applied. | Must |
| NFR-LEG-4 | Use of each AI provider's API and of Binance's API complies with their terms. Provider and exchange names are used only to identify them, with no suggestion of endorsement, and official logos are used only where their brand terms allow. | Must |
| NFR-LEG-5 | The product name is cleared for trademark conflicts before launch (see risk R8). | Must |

---

## 6. Logical data model

These are logical entities, not a physical schema. The physical schema follows from the code-first model (NFR-DAT-2).

| Entity | Purpose | Main attributes |
|---|---|---|
| User | An account. | Email, password hash, two-factor secret (encrypted), theme preference, motion preference, accepted terms versions, status. |
| AiCredential | A user's key for one AI provider. | User, provider, encrypted value, master key id, masked suffix, status, last validated, accessible models. |
| ExchangeCredential | A user's Binance key. | User, environment (Live or Demo), encrypted value, master key id, permissions, status, last validated. |
| AiProvider | One of the four providers. | Code, display name. |
| AiModel | A catalog entry. | Provider, model identifier, display name, metadata, capability flags, eligibility, status, first seen, last seen. |
| CatalogSyncRun | One sync attempt. | Provider, start, end, result, counts, error. |
| Agent | A trading agent. | User, name, credential, mode, market, symbol, timeframe, allocated capital, risk settings, state, state cause. |
| AgentRole | One role assignment. | Agent, role, model. |
| PromptTemplate | A versioned template. | Kind (role, market, timeframe, or profile), version, text, active flag. |
| DecisionCycle | One cycle. | Agent, candle close time, start, end, outcome, data package summary. |
| RoleOutput | One role call. | Cycle, role, model, template versions, structured result, rationale, tokens, latency, validation outcome. |
| RiskCheck | One Risk Guard result. | Cycle, outcome of each rule, computed quantity and leverage, verdict. |
| Order | An exchange order. | Agent, cycle, client order id, exchange order id, type, side, price, quantity, status. |
| Fill | An execution. | Order, price, quantity, fee, time. |
| Position | One position from open to close. | Agent, side, entry, quantity, stop-loss, take-profit, open time, close time, exit reason, net result, fees, funding, result in R. |
| ShadowTrade | The simulated result of a vetoed plan. | Cycle, outcome, result in R. |
| EquitySnapshot | Agent equity over time. | Agent, time, equity. |
| Lesson | A post-trade lesson. | Agent, position, text, tags, enabled flag. |
| ExperienceStat | An aggregated track record. | Model, role, market, timeframe, mode, scope (user or platform), period, counts, measures, strength score. |
| Notification | An in-app message. | User, type, content, read flag. |
| AuditEvent | A security- or money-relevant action. | User, action, target, time, origin. |
| RuntimeFlag | Operator runtime state. | Name (for example the global halt), value, changed by, changed at. |

---

## 7. Configuration file outline

The outline below shows what the single configuration file contains. Key names and the JSON form are illustrative; section 5.2 is the requirement. Three-value lists are minimum, default, and maximum.

```json
{
  "Application": { "Name": "Ecnanib", "PublicUrl": "", "RegistrationOpen": true, "Operators": [] },
  "ConnectionStrings": { "EcnanibDbContext": "" },
  "Security": { "ActiveKeyId": "", "MasterKeys": {}, "SessionIdleMinutes": 60 },
  "Logging": {
    "MinimumLevel": "Information",
    "Overrides": {},
    "Outputs": { "Console": {}, "File": { "Path": "", "Rolling": "Day", "RetainedFiles": 30 } }
  },
  "Email": { "Host": "", "Port": 0, "User": "", "Password": "", "From": "" },
  "AiProviders": {
    "Claude": { "BaseUrl": "", "CatalogKey": "", "TimeoutSeconds": 60, "Allow": [], "Deny": [], "LiveSearch": [] },
    "Gemini": { "BaseUrl": "", "CatalogKey": "", "TimeoutSeconds": 60, "Allow": [], "Deny": [], "LiveSearch": [] },
    "GPT": { "BaseUrl": "", "CatalogKey": "", "TimeoutSeconds": 60, "Allow": [], "Deny": [], "LiveSearch": [] },
    "Grok": { "BaseUrl": "", "CatalogKey": "", "TimeoutSeconds": 60, "Allow": [], "Deny": [], "LiveSearch": [] }
  },
  "CatalogSync": { "IntervalHours": 24, "MissedSyncsBeforeUnavailable": 2 },
  "Binance": {
    "Spot": { "Live": { "BaseUrl": "" }, "Demo": { "BaseUrl": "" } },
    "Futures": { "Live": { "BaseUrl": "" }, "Demo": { "BaseUrl": "" } },
    "Symbols": [],
    "OutboundIps": [],
    "LimitSharePercent": 70,
    "ReconcileMinutes": 5
  },
  "Trading": {
    "DefaultRoles": {
      "MarketAnalyst": "GPT",
      "SentimentAnalyst": "Grok",
      "Strategist": "Gemini",
      "RiskManager": "Claude"
    },
    "Aggressive": {
      "RiskPerTradePercent": [0.5, 2, 5],
      "MinRewardToRisk": 1.5,
      "LeverageCap": [1, 5, 10],
      "LiquidationBuffer": 2.0,
      "DailyLossPercent": [2, 6, 15],
      "DrawdownPercent": [10, 25, 50],
      "MaxEntryDistancePercent": [0.1, 1, 3]
    },
    "StaleSeconds": { "15m": 120, "1H": 300, "4H": 600, "1D": 900 }
  },
  "Experience": {
    "LessonsPerPrompt": 5,
    "MinSamples": 20,
    "Damping": 20,
    "Board": { "MinUsers": 5, "MinTrades": 50 }
  },
  "Retention": { "TranscriptDays": 90, "DecisionDays": 365, "NotificationDays": 90, "AuditDays": 730 },
  "Limits": {
    "MaxAgentsPerUser": 10,
    "SignIn": { "PerClient": 30, "PerAddress": 10, "Minutes": 15 },
    "Registration": { "PerClient": 10, "PerAddress": 3, "Minutes": 60 },
    "PasswordReset": { "PerClient": 10, "PerAddress": 3, "Minutes": 60 }
  }
}
```

---

## 8. Traceability to the brief

| Brief item | Covered by |
|---|---|
| C1. Named Ecnanib | Section 1.2; section 7 (Application name); NFR-LEG-5 |
| C2. 15m, 1H, 4H, or 1D per agent, different prompt | FR-AGT-3, FR-PRM-2, FR-PRM-4, FR-CYC-1, FR-CYC-2 |
| C3. Aggressive | FR-RSK-1 to FR-RSK-9, FR-PRM-5, Appendix B |
| C4. AI provider models catalog syncing | FR-CAT-1 to FR-CAT-11 |
| C5. AI providers trading experience | FR-EXP-1 to FR-EXP-11, FR-TEAM-4 |
| C6. Animation | FR-ANI-1 to FR-ANI-7 |
| C7. Binance, Spot, Futures, different prompt | FR-BIN-1 to FR-BIN-11, FR-BIN-S1 to FR-BIN-S3, FR-BIN-F1 to FR-BIN-F6, FR-AGT-4, FR-PRM-3, FR-PRM-4 |
| C8. BYOK, encrypt | FR-KEY-1 to FR-KEY-11, NFR-SEC-5 |
| C9. Four providers together, role by strength, model per agent | FR-TEAM-1 to FR-TEAM-10, FR-AGT-5, FR-EXP-4, Appendix A |
| C10. Dark, light, system; system by default | FR-THM-1 to FR-THM-5 |
| C11. Logging library like Serilog | NFR-LOG-1 to NFR-LOG-7 |
| C12. PostgreSQL, auto-migrate, no partition, code-first ORM | NFR-DAT-1 to NFR-DAT-5, NFR-DAT-10 |
| C13. Simple architecture | NFR-ARC-1 to NFR-ARC-6 |
| C14. Single config file, no env, no vault | NFR-CFG-1 to NFR-CFG-7, section 7 |
| C15. SaaS, free, no subscription, no tenant, users | FR-ACC-5, FR-ACC-6, FR-ACC-7, section 2.1 |

---

## 9. Risks and open questions

### 9.1 Risks

| # | Risk | Treatment |
|---|---|---|
| R1 | **Secrets in one plain file.** With no vault and no environment variables, the master encryption keys and the database password sit in the configuration file. Anyone who can read that file and the database can decrypt every user's exchange and AI keys. | Restricted file permissions (NFR-CFG-5), key rotation (FR-KEY-7), refusal of keys that can withdraw (FR-KEY-4), and encouragement of IP-restricted keys. The owner should accept this risk explicitly. |
| R2 | **Shared exchange limits.** Binance counts request limits per IP address, so all users share one budget, and IP-restricted keys need the service to have fixed outbound addresses. | Shared market data (FR-BIN-3), a service-wide limiter (FR-BIN-4), fixed outbound addresses, and load testing (NFR-PRF-3). Capacity has a ceiling per address. |
| R3 | **Unproven trading ability.** Public tests through October 2026 show no AI model with a reliable trading edge, and results change between tests and model versions. The aggressive profile magnifies losses as well as gains. | Demo by default (FR-AGT-7), the Risk Guard (FR-RSK-3), measured experience instead of assumptions (FR-EXP-4), and clear disclosure (NFR-LEG-1). |
| R4 | **Regulation.** A service that trades users' funds automatically may need licensing or registration in some countries, and Binance products are not available everywhere. | Legal review before launch (NFR-LEG-3). |
| R5 | **Model churn and provider outages.** Models are retired and renamed often, and providers have outages and change their terms. | Catalog sync (section 4.3), no silent substitution (FR-CAT-8), and safe failure (NFR-REL-2). |
| R6 | **Prompt injection.** News and social content can contain text written to manipulate AI models. | Untrusted-data handling (FR-PRM-8, NFR-SEC-7), no direct order path for models (NFR-SEC-6), and the Risk Guard. |
| R7 | **Unpartitioned growth.** Decision records and transcripts grow with every cycle. | Retention jobs (NFR-DAT-5), no market-data storage (NFR-DAT-6), and table-size monitoring (FR-OPS-2). |
| R8 | **Product name.** "Ecnanib" is "Binance" reversed, which may raise trademark or affiliation concerns. | Clearance before launch (NFR-LEG-5). |
| R9 | **Cost to users.** Four role calls per cycle on a 15m timeframe is about 384 calls per agent per day, charged to the user's own AI keys. | Usage display (FR-RPT-3, FR-KEY-11). |

### 9.2 Open questions

| # | Question |
|---|---|
| Q1 | Is the reading of "AI providers trading experience" right: a measured track record per provider and model, plus lessons fed back into prompts? Should the platform-wide board (FR-EXP-10) exist? |
| Q2 | Is the reading of "animation" right: motion that shows live state, centered on the team pipeline (FR-ANI-1)? |
| Q3 | Does "futures" mean USDⓈ-margined perpetual contracts only? |
| Q4 | Must every agent use all four providers, or may a user with fewer keys run a reduced team (FR-TEAM-5)? |
| Q5 | Should users be able to edit prompts or add their own instructions per agent? |
| Q6 | Are the aggressive defaults and bounds in Appendix B acceptable? |
| Q7 | Does "single config file" exclude per-environment variants, as assumed in NFR-CFG-2? |
| Q8 | Is the Operator role acceptable, and should operators be defined in the configuration file (FR-OPS-1)? |
| Q9 | Is email and password the only sign-in method, or should sign-in through an external identity provider be added? |
| Q10 | Is it acceptable for the Sentiment Analyst to rely on each provider's own live search (FR-CYC-3), or should Ecnanib supply a news feed? |
| Q11 | Which symbols are offered at launch, and is one symbol per agent acceptable? |
| Q12 | What is the default role mapping at launch (Appendix A)? |

---

## Appendix A. Default role mapping

This appendix is not a requirement. It explains the initial values for FR-TEAM-3. They are a starting hypothesis, to be replaced by measured strength (FR-EXP-4) as soon as there is enough data.

| Role | Initial provider | Reason | Strength of evidence |
|---|---|---|---|
| Sentiment Analyst | Grok | It has built-in access to live posts on X, where much crypto news and sentiment appears first. | Good: a documented product capability. |
| Risk Manager | Claude | It ranked best for risk-adjusted results, with a maximum drawdown of 8.7%, in a 90-day simulated crypto benchmark (July to October 2026). A reported tendency to favor long positions argues against making it the Strategist. | Weak: one benchmark and one model version. |
| Strategist | Gemini | It had the highest return of the four providers in the same benchmark, and was reported as comfortable taking short positions, which matters on Futures. | Weak: one benchmark; it was among the worst performers in an earlier real-money test. |
| Market Analyst | GPT | The remaining role. Public tests give no clear evidence for or against. | None. |

Public tests disagree with each other, and each result belongs to a specific model version. In the 90-day benchmark above, none of the four providers' models beat simply holding Bitcoin. See the references.

## Appendix B. Aggressive profile settings

These values are placeholders **to confirm**. They are deliberately above the 0.5% to 1% risk per trade that is conventional for retail trading, which is what makes the profile aggressive, and they make large losses more likely.

| Setting | Default | Hard bounds |
|---|---|---|
| Risk per trade, as a share of agent equity | 2% | 0.5% to 5% |
| Minimum reward-to-risk ratio at entry | 1.5 | 1.0 or higher |
| Leverage cap on Futures | 5x | 1x to 10x |
| Liquidation buffer factor on Futures | 2.0 | 1.5 or higher |
| Maximum position value on Spot | 100% of agent equity | Fixed |
| Open positions per agent | 1 | Fixed |
| Daily loss limit, as a share of agent equity | 6% | 2% to 15% |
| Drawdown limit from peak agent equity | 25% | 10% to 50% |
| Maximum distance of entry from current price | 1% | 0.1% to 3% |
| Stale limit after candle close | 15m: 120 s; 1H: 300 s; 4H: 600 s; 1D: 900 s | Set in the configuration file |

## References

These informed the constraints and the appendix. They are not part of the requirements.

- [Binance Developer Documentation: Introduction](https://developers.binance.com/en/docs/introduction)
- [Binance Spot: General REST API Information](https://developers.binance.com/en/docs/products/spot/rest-api) (key types, signed requests, per-IP limits)
- [Binance: Demo Mode for Spot Trading](https://developers.binance.com/en/docs/products/spot/demo-mode/general-info)
- [Binance Futures: General Info](https://developers.binance.com/docs/derivatives/futures-data/general-info)
- [OAK Research: AI Trading Benchmark, Season 1](https://oakresearch.io/en/ai/trading-benchmark/s1) (90-day simulated benchmark, July to October 2026)
- [SimianX: Best AI Model for Crypto Trading in 2026](https://www.simianx.ai/stories/best-ai-model-for-crypto-trading)
- [ThinkAdvisor: Why AI bots aren't ready to replace your fund manager](https://www.thinkadvisor.com/amp/2026/05/06/why-ai-bots-arent-ready-to-replace-your-fund-manager/) (reported tendencies of each model)
- [AI Toolbox: Grok for Traders](https://www.ai-toolbox.co/advantages-use-cases-grok-toolbox/grok-for-traders-real-time-x-2026) (live access to X)
- [arXiv: When Agents Trade (Agent Market Arena)](https://arxiv.org/abs/2510.11695)
- [arXiv: AI-Trader](https://arxiv.org/abs/2512.10971v1)
