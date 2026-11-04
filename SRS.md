# Ecnanib — Software Requirements Specification

**AI Cryptocurrency Trading Agents**

| | |
|---|---|
| Document | Software Requirements Specification (SRS) |
| Product | Ecnanib |
| Version | 1.0 — Draft |
| Date | 2026-11-01 |
| Status | For review |

> **Technology neutrality.** This SRS does not prescribe programming languages, frameworks, UI libraries, hosting platforms or third-party packages. The only technology-level constraints are those explicitly requested by the stakeholder (Section 2.6): PostgreSQL, a code-first ORM with automatic migrations, a structured logging library comparable to Serilog, and a single appsettings-style configuration file.

## Table of Contents

1. Introduction
2. Overall Description
3. Functional Requirements
4. External Interface Requirements
5. Non-Functional Requirements
6. Data Requirements
7. Configuration File
8. Key Use Cases
9. Acceptance Criteria
10. Risks and Mitigations
11. Open Questions
- Appendix A — Prompt Overlay Requirements
- Appendix B — Role Output Contracts

---

## 1. Introduction

### 1.1 Purpose

This document specifies the requirements for **Ecnanib**, a free, multi-user web application (software as a service) in which users create AI trading agents that trade cryptocurrency on Binance Spot and Binance USDⓈ-M Futures. Each agent is driven by a council of large language models from Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok), using the user's own API keys.

Intended readers: product owner, developers, testers and operators.

### 1.2 Product Scope

**In scope**

- Self-service user accounts; free; no subscriptions, plans or payments; no tenants or organizations.
- Bring-your-own-key (BYOK) storage of AI provider and Binance credentials, encrypted at rest.
- An AI model catalog synchronized from each provider.
- Trading agents with per-agent selection of market (Spot or Futures), timeframe (15m, 1H, 4H, 1D), environment (Demo or Live), trading pair, risk limits, and provider/model per AI role.
- A multi-provider AI council in which models collaborate in roles assigned by strength.
- Layered prompts that differ by role, market type and timeframe.
- A deterministic risk engine, order execution and position management on Binance.
- Tracking of each AI provider's and model's trading experience (performance metrics and reflective memory).
- An animated web UI with Dark, Light and System themes (System by default).
- Structured logging, an audit trail, PostgreSQL persistence with automatic code-first migrations, and a single configuration file.

**Out of scope (version 1)**

- Exchanges other than Binance (including Binance.US) and Binance products other than Spot and USDⓈ-M Futures (no margin, COIN-M, options, earn or convert).
- Withdrawals or any other transfer of funds.
- Billing, subscriptions, usage plans, multi-tenancy, teams or shared workspaces.
- A backtesting engine, strategy marketplace, copy trading or social features.
- Native mobile or desktop apps (the web UI is responsive).
- Platform-provided AI credentials (strictly BYOK).

### 1.3 Definitions and Acronyms

| Term | Definition |
|---|---|
| Agent | A user-configured automated trader bound to one trading pair, one market type, one timeframe and one environment. |
| AI Council | The set of AI roles that collaborate to produce a decision for an agent. |
| Role | A function in the council (Technical Analyst, Sentiment Analyst, Strategist, Risk Critic, Reviewer) filled by one provider model. |
| Provider | An AI vendor: Anthropic (Claude), Google (Gemini), OpenAI (GPT) or xAI (Grok). |
| Model catalog | The synchronized list of models available from providers, with metadata. |
| BYOK | Bring Your Own Key: users supply their own API credentials. |
| Decision cycle | One scheduled run of an agent's pipeline, triggered when a candle of the agent's timeframe closes. |
| Proposal | A structured trade intent produced by the Strategist. |
| Risk Engine | The deterministic (non-AI) component that validates and sizes every order intent. |
| HTF | Higher timeframe used for context (for example, 1H context for a 15m agent). |
| Spot | Buying and selling the actual asset; long-only, no leverage. |
| USDⓈ-M Futures | Binance stablecoin-margined perpetual futures; long or short, with leverage. |
| Demo / Live | Binance demo trading environment (simulated funds, realistic prices) / real-money environment. |
| Funding rate | Periodic payment between longs and shorts on perpetual futures. |
| Liquidation price | The price at which the exchange force-closes a leveraged position. |
| Protective stop | A stop order resting on the exchange that closes a position at a predefined loss. |
| R / R-multiple | Profit or loss expressed in multiples of the initial risk (entry-to-stop distance × quantity). |
| Kill switch | An action that cancels an agent's open orders, closes its positions and stops it. |
| Reflection | A structured lesson written by the Reviewer role after a trade closes; part of the trading-experience memory. |
| Shadow outcome | The hypothetical result of a proposal that was not executed, simulated on subsequent candles. |
| Exchange filters | Exchange-defined order rules per symbol (tick size, step size, minimum notional, etc.). |

### 1.4 Conventions

- **shall** = mandatory; **should** = recommended; **may** = optional.
- Requirement IDs: `FR-<AREA>-<nn>` (functional), `NFR-<AREA>-<nn>` (non-functional), `EXT-<AREA>-<nn>` (external interfaces), `CON-<nn>` (constraints), `ASM-<nn>` (assumptions), `CFG-<nn>` (configuration rules), `DATA-R-<nn>` (data rules), `AC-<nn>` (acceptance criteria).
- "The system" means Ecnanib. All times are UTC unless stated otherwise.

### 1.5 References

- Binance Spot API documentation (general API information, rate limits, Demo Mode) and Binance USDⓈ-M Futures API documentation.
- Public API documentation of Anthropic, Google Gemini, OpenAI and xAI.
- OWASP Top 10 and OWASP ASVS; WCAG 2.1.

### 1.6 Stakeholder Request Traceability

| Stakeholder request | Where addressed |
|---|---|
| AI cryptocurrency trading agents named Ecnanib | 1.1, 3.5, CON-14 |
| 15m / 1H / 4H / 1D selectable per agent, different prompt | FR-AGT-02, 3.7, Appendix A.1 |
| AI provider model catalog syncing | 3.4 |
| AI providers trading experience | 3.12 |
| Animation | 3.15 |
| Binance Spot and Futures, different prompt | CON-12, 3.7, 3.9, 3.10, Appendix A.2 |
| BYOK, encrypted | 3.3, NFR-SEC-05 |
| Claude, Gemini, GPT and Grok work together, roles by strength, provider model selected per agent | 3.6 |
| Dark / Light / System, System by default | FR-PREF-01 to FR-PREF-03 |
| Logging library like Serilog | CON-07, 3.17 |
| PostgreSQL, auto-migrate, no partitioning, code-first ORM | CON-02 to CON-05, 4.5, 5.8, 6.2 |
| Simple architecture | CON-08, 2.2 |
| Single config file like appsettings, no environment variables, no vault | CON-06, Section 7 |
| SaaS, free, no subscription, no tenants, users | CON-09, CON-10, 2.4, 3.1 |

---

## 2. Overall Description

### 2.1 Product Perspective

Ecnanib is a new, self-contained product deployed as one application with one PostgreSQL database and one configuration file. It calls Binance and the four AI providers over HTTPS using credentials supplied by each user.

```
                   +------------------------------------------+
  Browser  ----->  |                 Ecnanib                  |  ---> Binance Spot API (Live / Demo)
  (Users,          |  Web UI + API + Scheduler + Workers      |  ---> Binance USD-M Futures API (Live / Demo)
   Admins)         |         (single deployable unit)         |  ---> Anthropic API (Claude)
                   +--------------------+---------------------+  ---> Google API (Gemini)
                                        |                        ---> OpenAI API (GPT)
                          +-------------+--------------+         ---> xAI API (Grok)
                          |                            |         ---> SMTP server (optional)
                     PostgreSQL               Configuration file
                                              (appsettings-style)
```

### 2.2 Logical Architecture

The system is a modular monolith: one process containing the modules below, communicating in-process. There is no message broker, distributed cache or separate job server. Background work (scheduling, decision cycles, reconciliation, catalog sync, purges) runs inside the application, with durable state in PostgreSQL.

```
+--------------------------------------------------------------------------------+
|                                    Ecnanib                                     |
|                                                                                |
|  Web UI --- API ---+--- Identity & Preferences                                 |
|                    +--- Credentials (BYOK, encrypted at rest)                  |
|                    +--- Model Catalog & Sync                                   |
|                    +--- Agents --- Scheduler --- Decision Cycle                |
|                    |                                 |                         |
|                    |      Market Data <--------------+                         |
|                    |      AI Council (provider adapters, prompts) <------+     |
|                    |      Risk Engine (deterministic) <------------------+     |
|                    |      Execution & Reconciliation (exchange adapter)        |
|                    +--- Trading Experience (metrics, reflections)              |
|                    +--- Notifications                                          |
|                    +--- Administration                                         |
|                                                                                |
|  Cross-cutting: Structured Logging | Audit Trail | Configuration | Persistence  |
+--------------------------------------------------------------------------------+
```

### 2.3 Product Functions (Summary)

- Register, sign in, secure the account, and set theme and animation preferences.
- Add and validate encrypted AI provider and Binance credentials.
- Browse the synchronized AI model catalog.
- Create agents: choose market, environment, pair, timeframe, models per role, risk limits, execution mode and AI budget.
- Run scheduled decision cycles in which AI roles analyze, propose and critique; the Risk Engine validates and sizes; orders execute with protective stops.
- Approve trades manually (Confirm mode) or let agents run autonomously.
- Monitor agents live, inspect full decision traces, and view trades and P&L.
- Compare AI providers' and models' trading experience, and feed lessons back into prompts.
- Administer users, prompt templates, the catalog and system health.

### 2.4 User Classes

| Class | Description |
|---|---|
| Visitor | Unauthenticated. Can view the landing page and disclaimers, register and sign in. |
| User | A registered individual who owns their credentials, agents and data. Expected to understand basic trading concepts; not expected to be a developer. |
| Administrator | An operator account designated in the configuration file. Manages prompt templates, the catalog, users and system health. Has no access to users' decrypted secrets. |

There are no tenants. Every user is an independent account, and no data is shared between users except opt-in anonymized aggregates (FR-EXP-07).

### 2.5 Operating Environment

- **Server:** any operating system that can run the application, reach PostgreSQL and make outbound HTTPS requests.
- **Database:** PostgreSQL (a currently supported major version).
- **Client:** current versions of major desktop and mobile browsers.
- **Network:** all Binance requests leave from the server's egress IP address(es), which all users share. This matters for exchange rate limits (EXT-BIN-04) and for users who whitelist IP addresses on their Binance keys (FR-KEY-05).

### 2.6 Design and Implementation Constraints

- **CON-01** This SRS prescribes no technology stack beyond CON-02 to CON-08.
- **CON-02** The only database is PostgreSQL.
- **CON-03** Persistence uses a code-first ORM: the schema is defined in application code, and migrations are generated from it.
- **CON-04** Pending migrations are applied automatically at application startup.
- **CON-05** No table partitioning is used.
- **CON-06** All configuration comes from exactly one appsettings-style JSON file. No environment variables, no external secret vault and no other configuration sources.
- **CON-07** Logging uses a structured logging library comparable to Serilog: message templates with named properties, multiple configurable sinks, enrichers, per-category level overrides, and masking/destructuring policies.
- **CON-08** Simple architecture: a single deployable modular monolith; no microservices, message brokers, distributed caches or separate job servers.
- **CON-09** The service is free: no billing, subscription, plan or quota-monetization features.
- **CON-10** No multi-tenancy: data is owned by individual users.
- **CON-11** BYOK: the platform holds no AI or exchange credentials of its own for trading.
- **CON-12** Exchange support is limited to Binance Spot and Binance USDⓈ-M Futures, each with Live and Demo environments.
- **CON-13** Supported AI providers are Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok).
- **CON-14** The product name is Ecnanib. Branding shall not imitate Binance's name styling, logo, colors or trade dress, and the UI shall state that the product is not affiliated with Binance.

### 2.7 Assumptions and Dependencies

- **ASM-01** Users are responsible for being eligible to use Binance in their jurisdiction (for example, Binance.com does not serve US residents). The system does not verify eligibility.
- **ASM-02** Users obtain and pay for their own AI provider and exchange usage.
- **ASM-03** Binance and AI provider APIs remain available; breaking API changes require system updates.
- **ASM-04** Providers expose model listings and token usage through their APIs. Pricing and some capability metadata may need manual maintenance.
- **ASM-05** Before public launch, the operator obtains legal review of: regulatory obligations for offering automated trading tools in target jurisdictions; each AI provider's usage policies for automated financial decision-making; data-protection obligations; and clearance of the product name.
- **ASM-06** A single application instance is the baseline deployment; horizontal scaling is not required in version 1.
- **ASM-07** Binance provides demo environments for Spot and USDⓈ-M Futures whose base URLs can be configured.

### 2.8 Guiding Principles

1. **AI proposes, rules decide.** No order reaches the exchange without passing the deterministic Risk Engine. Hard limits are never delegated to a model.
2. **Protection lives on the exchange.** Every open position has a protective stop resting on Binance, so an Ecnanib outage never leaves a position unprotected.
3. **Demo first.** New agents start in Demo; Live requires explicit opt-in.
4. **Full transparency.** Every decision is traceable: inputs, prompts, responses, risk checks and orders.
5. **Least privilege.** Exchange keys must not have withdrawal permission.
6. **Simplicity.** One process, one database, one configuration file.
7. **Provider neutrality.** No provider is assumed superior. Default role assignments are hypotheses that the system's own trading-experience data should confirm or overturn.

---

## 3. Functional Requirements

### 3.1 Accounts and Authentication (AUTH)

- **FR-AUTH-01** Visitors shall be able to register with an email address and password.
- **FR-AUTH-02** Registration shall require acceptance of the Terms of Use and the Risk Disclosure. The system shall record the accepted document versions and timestamps.
- **FR-AUTH-03** When email is enabled in configuration, the system shall verify email ownership through a time-limited link, and email verification shall be required before an agent can be switched to Live.
- **FR-AUTH-04** Users shall sign in with email and password. Failed attempts shall return a generic error and be throttled; accounts shall be temporarily locked after a configurable number of failures.
- **FR-AUTH-05** Users should be able to enable time-based one-time-password (TOTP) two-factor authentication. When enabled, it shall be required at sign-in and for sensitive actions: adding or rotating exchange credentials, switching an agent to Live, using the kill switch, and disabling two-factor authentication.
- **FR-AUTH-06** When email is enabled, users shall be able to reset a forgotten password through a single-use, time-limited link. When email is disabled, an administrator shall be able to generate a single-use reset link to deliver to the user out of band.
- **FR-AUTH-07** Sessions shall expire after configurable idle and absolute timeouts. Users shall be able to list and revoke their active sessions.
- **FR-AUTH-08** Changing the password shall require the current password and shall revoke all other sessions.
- **FR-AUTH-09** Users shall be able to delete their account. The system shall first stop all agents, letting the user choose whether to close or keep open positions, and then permanently delete credentials and personal data. Audit records shall be retained with the user identity anonymized.
- **FR-AUTH-10** Administrator rights shall be granted to accounts whose verified email address appears in the configuration file's administrator list. When email is disabled, nothing can verify an address, so a signed-in user with a listed address shall verify it by entering the bootstrap secret from the configuration file. Registering a listed address shall not by itself grant any rights.

### 3.2 User Preferences and Theme (PREF)

- **FR-PREF-01** Users shall be able to choose a theme: **Dark**, **Light** or **System**. The default shall be **System**.
- **FR-PREF-02** In System mode, the UI shall follow the operating system's color scheme and switch immediately when it changes, without a page reload.
- **FR-PREF-03** The theme preference shall be stored server-side per user and applied before first paint, so the wrong theme never flashes on load. Before sign-in, the UI shall use System.
- **FR-PREF-04** Users shall be able to choose an animation level: **System** (default), **Full**, **Reduced** or **Off**. System uses Reduced when the operating system requests reduced motion and Full otherwise.
- **FR-PREF-05** Users shall be able to set a display timezone (default: the browser's timezone).
- **FR-PREF-06** Users shall be able to choose which events produce email notifications (when email is enabled). In-app notifications are always on.

### 3.3 BYOK Credential Management (KEY)

- **FR-KEY-01** Users shall be able to add, label, validate, rotate and delete credentials for Anthropic, Google Gemini, OpenAI, xAI, Binance Live and Binance Demo.
- **FR-KEY-02** Binance credentials shall be stored per environment (Live or Demo). Validation shall record whether Spot trading and Futures trading are enabled for the key.
- **FR-KEY-03** Secrets shall be write-only. After saving, the full secret shall never be returned to the browser or displayed. The UI shows provider, label, environment, status, last four characters, created date, last validated and last used.
- **FR-KEY-04** Credentials shall be validated on save and on demand: AI keys with a lightweight authenticated call (such as listing models), Binance keys with an authenticated account request. Status values: Valid, Invalid, Insufficient permissions, Unverified.
- **FR-KEY-05** For Binance keys, the system shall read the key's permission restrictions where the exchange exposes them, and shall:
  - reject keys that have withdrawal permission enabled;
  - warn when the key has no IP restriction, and display the server's egress IP addresses (from configuration) for whitelisting;
  - refuse to use a key for a market it is not enabled for.
- **FR-KEY-06** The system shall support Binance HMAC keys and should support Ed25519 keys.
- **FR-KEY-07** Every secret (AI keys, Binance keys and secrets, TOTP secrets) shall be encrypted at rest with authenticated encryption (e.g., AES-256-GCM) using a 256-bit master key from the configuration file. Each ciphertext shall record the identifier of the key that encrypted it.
- **FR-KEY-08** Master-key rotation: when the configuration marks a new key active, the system shall re-encrypt all secrets in the background, report progress to administrators, and keep using older keys for decryption until re-encryption completes.
- **FR-KEY-09** Secrets shall be decrypted only in memory at the moment of use and shall never appear in logs, error messages, decision traces, exports or notifications.
- **FR-KEY-10** Deleting a credential that active agents use shall require confirmation and shall pause those agents.
- **FR-KEY-11** The system shall revalidate credentials periodically (configurable). On failure, it shall pause the affected agents (their protective orders stay on the exchange) and notify the user.

### 3.4 AI Model Catalog Synchronization (CAT)

- **FR-CAT-01** The system shall maintain a catalog of models per provider with: provider, model ID, display name, family, created or release date (if available), context window, maximum output, capability flags (image input, structured/JSON output, tool use, provider web or X search, reasoning-effort control), status (Active, Deprecated, Retired, Hidden), input and output price per million tokens (if known), and first-seen and last-seen timestamps.
- **FR-CAT-02** Because model availability can differ by account, the system shall sync using each user's validated credentials, record per-credential availability, and merge the results into a global catalog.
- **FR-CAT-03** Sync shall run: (a) when a credential is added or rotated; (b) on a configurable schedule; (c) on demand — by a user for their own credentials, or by an administrator for all.
- **FR-CAT-04** A model that a provider stops returning shall be marked Deprecated, then Retired after a configurable grace period. Agents using it shall be warned on deprecation. On retirement, the role switches to its fallback model or, without a valid fallback, the agent is paused. The user is notified in both cases.
- **FR-CAT-05** Administrators shall be able to override display name, visibility, capability flags, pricing and recommended roles. Overrides shall persist across syncs.
- **FR-CAT-06** Metadata that providers do not expose (such as pricing) may be supplied through the configuration file or the admin UI.
- **FR-CAT-07** The catalog UI shall filter by provider, capability and status, and shall show which of the user's credentials can access each model.
- **FR-CAT-08** Every sync run shall be recorded with its trigger, duration, counts of added, updated, deprecated and retired models, and errors.

### 3.5 Trading Agents (AGT)

- **FR-AGT-01** Users shall be able to create, view, edit, clone, archive and delete agents, up to a configurable maximum per user.
- **FR-AGT-02** Agent configuration:

| Field | Values / rules |
|---|---|
| Name, description | Free text; name unique per user |
| Market type | Spot or USDⓈ-M Futures |
| Environment | Demo (default) or Live |
| Trading pair | One symbol per agent, validated against the exchange's tradable symbols |
| Timeframe | 15m, 1H, 4H or 1D |
| Execution mode | Autonomous or Confirm each trade |
| Auto-execute risk-reducing actions | On (default) or Off; applies in Confirm mode (FR-APR-04) |
| Role assignments | Provider and model per role, plus an optional fallback model per role (3.6) |
| Capital allocation | Maximum quote-currency amount the agent may commit |
| Risk settings | See FR-RISK-02 |
| AI budget | Maximum estimated AI spend per day and per month, or token caps when pricing is unknown |
| Experience memory | On/off, and the number of reflections included in prompts |
| Custom instructions | Optional free text appended to prompts, length-limited (FR-PRM-05) |

- **FR-AGT-03** Market type, environment, trading pair and timeframe shall not be changeable while the agent has open positions or open orders.
- **FR-AGT-04** Agent states are Draft, Active, Paused, Stopped, Error and Archived. Every state change shall be audited with actor and reason.
- **FR-AGT-05** Activation shall require valid credentials for the agent's environment and market, every required role assigned to an active model accessible with the user's credentials, and complete risk settings. Switching to Live shall additionally require a verified email (when email is enabled), two-factor authentication when the configuration requires it for Live, and explicit acknowledgement of the Risk Disclosure and the chosen execution mode.
- **FR-AGT-06** Pausing shall stop new decision cycles. Open positions and their exchange-resting protective orders remain in place, and reconciliation continues.
- **FR-AGT-07** Stopping shall end decision cycles and let the user choose either to close open positions and cancel open orders, or to leave them as they are.
- **FR-AGT-08** A kill switch shall exist per agent, and as "Stop all agents" per user. It cancels all open orders the agent(s) created, closes their positions at market, and stops them. It requires confirmation (and two-factor authentication when enabled).
- **FR-AGT-09** An agent shall manage only orders and positions it created, identified by its client order ID prefix, and shall never modify the user's manual orders. On Spot, an agent may sell only the base-asset quantity acquired by its own fills.
- **FR-AGT-10** The system shall prevent more than one active agent per combination of exchange credential, market type and trading pair, because exchange positions and balances for a symbol are shared within an account.
- **FR-AGT-11** The agent overview shall show status, next cycle time, current position, realized and unrealized P&L (net of fees and funding), AI cost to date, and the last decision summary.
- **FR-AGT-12** Cloning shall create a new Draft agent with the same configuration and none of the history.
- **FR-AGT-13** The system should warn when the total capital allocation of a credential's active agents exceeds that account's available balance or margin.

### 3.6 AI Council and Roles (ROLE)

- **FR-ROLE-01** Each decision cycle shall use the following roles:

| Role | Responsibility | Output (Appendix B) | Required |
|---|---|---|---|
| Technical Analyst | Analyze price action, computed indicators and higher-timeframe structure | Technical assessment | Yes |
| Sentiment Analyst | Assess current news and social sentiment for the base asset, using the provider's live web or X search where available | Sentiment assessment | No |
| Strategist | Combine the analyses, account and position state, risk settings and reflections into a decision | Trade proposal | Yes |
| Risk Critic | Challenge the proposal: its logic, its consistency with the analyses, the rules and recent reflections | Risk verdict | Yes, for trade actions |
| Reviewer | After a trade closes, analyze the whole trade lifecycle and write a lesson | Reflection | No (on by default) |

- **FR-ROLE-02** For each role, users shall select a provider and model from the catalog, limited to models accessible with their credentials that have the capabilities the role requires (structured output for all roles; live search preferred for the Sentiment Analyst; image input optional for the Technical Analyst).
- **FR-ROLE-03** Default role assignments by strength shall be defined in the configuration file and be editable by administrators; users can override them per agent. Initial defaults:

| Role | Default provider | Rationale |
|---|---|---|
| Sentiment Analyst | xAI Grok | Native access to real-time X and web data |
| Technical Analyst | Google Gemini | Native multimodal input (can read rendered charts alongside numeric data) and long context |
| Strategist | OpenAI GPT | Strong general reasoning and structured outputs |
| Risk Critic | Anthropic Claude | Thorough long-form critique and constraint checking |
| Reviewer | Google Gemini | Long-context review of the complete trade lifecycle, including charts |

  These defaults reflect commonly cited strengths at the date of this document. They shall be re-evaluated using the system's own trading-experience data (3.12).
- **FR-ROLE-04** By default, the Risk Critic shall use a different provider from the Strategist. Using the same provider shall be allowed, with a warning.
- **FR-ROLE-05** An agent shall be able to run with a single provider filling all roles, for users who hold only one AI key.
- **FR-ROLE-06** On timeout, error, rate limit or refusal, a role shall retry per configuration and then use its fallback model. If that also fails, an optional role is skipped; a failed required role makes the cycle outcome HOLD (no new entries), while management of existing protective orders continues.
- **FR-ROLE-07** The system should offer "Suggest assignment", which recommends a model per role based on the user's accessible models and trading-experience metrics.
- **FR-ROLE-08** The system shall expose each role's state per cycle (Queued, Running, Done, Failed, Skipped) and its summary output to the UI for the animated council view (FR-ANI-01).

### 3.7 Prompt Management (PRM)

- **FR-PRM-01** Prompts shall be composed from layers, in this order:
  1. System layer: safety rules, untrusted-content handling and the output contract (not editable by users);
  2. Role template;
  3. Market overlay (Spot or Futures);
  4. Timeframe overlay (15m, 1H, 4H or 1D);
  5. Agent custom instructions (from the user);
  6. Runtime context: market data, computed indicators, account and position state, risk settings and recent reflections.
- **FR-PRM-02** Every combination of role × market × timeframe shall resolve to a complete prompt through layer composition. Administrators shall be able to override any specific combination.
- **FR-PRM-03** Templates shall be versioned. Administrators shall be able to edit, preview with sample data, publish and roll back versions. Every AI call shall record the template versions it used.
- **FR-PRM-04** Initial templates shall be seeded into the database as code-first seed data on first startup.
- **FR-PRM-05** Custom instructions shall be inserted in a clearly delimited section, shall be length-limited, and shall not override the system layer, risk limits or output contract.
- **FR-PRM-06** Untrusted external content (news, social posts, web results) shall be delimited and labeled as data, and prompts shall instruct models never to follow instructions found inside it.
- **FR-PRM-07** Market and timeframe overlays shall cover at least the content listed in Appendix A.
- **FR-PRM-08** Every AI response shall conform to its role's output contract (Appendix B). Provider-native structured output shall be used where available.

### 3.8 Decision Cycle (CYC)

- **FR-CYC-01** Each active agent shall run a decision cycle when a candle of its timeframe closes, aligned to UTC (15m: :00, :15, :30, :45; 1H: hourly; 4H: 00:00, 04:00, …, 20:00; 1D: 00:00), plus a configurable delay to make sure the candle is final.
- **FR-CYC-02** A cycle shall perform these steps:

```
 1. Acquire the agent lock (skip and log if the previous cycle is still running)
 2. Reconcile with the exchange: balances, open orders, position, fills since last cycle
 3. Collect market data: candles (agent timeframe + HTF), price, top of book;
    Futures: mark price, funding rate and next funding time, position liquidation price
 4. Compute indicators deterministically (e.g., EMA 20/50/200, RSI 14, ATR 14,
    recent swing highs/lows, volume statistics)
 5. Technical Analyst  ||  Sentiment Analyst            (in parallel)
 6. Strategist  --> HOLD ................................ end (trace saved)
       | trade action
 7. Risk Critic --> REJECT .............................. end (trace saved)
       | APPROVE / MODIFY
 8. Risk Engine (sizing + hard limits) --> FAIL ......... end (trace saved)
       | sized order intent
 9. Autonomous: execute              Confirm: create pending approval
10. Persist the decision trace, update metrics, send notifications
```

- **FR-CYC-03** Allowed actions:

| Spot | USDⓈ-M Futures |
|---|---|
| BUY (open or add to a long) | OPEN_LONG, OPEN_SHORT |
| SELL (close or reduce) | CLOSE (full or partial, reduce-only) |
| ADJUST_STOP (risk-reducing only) | ADJUST_STOP (risk-reducing only) |
| ADJUST_TARGET | ADJUST_TARGET |
| CANCEL_PENDING | CANCEL_PENDING |
| HOLD | HOLD |

- **FR-CYC-04** If the system was unavailable at a scheduled time, on startup it shall run at most one catch-up cycle per agent when within a configurable grace window; otherwise the cycle is skipped and logged.
- **FR-CYC-05** If an AI response fails output-contract validation, the system shall retry once, including the validation errors in the request. If it fails again, the role is treated as failed (FR-ROLE-06).
- **FR-CYC-06** Each cycle shall have a deadline per timeframe (configurable). After the deadline, no new entry is executed in that cycle.
- **FR-CYC-07** When an agent's AI budget for the period is exhausted, the agent shall enter protective-only mode (no new entries; existing protective orders are still managed) and the user shall be notified.
- **FR-CYC-08** The system shall limit concurrent cycles (configurable), queue the rest fairly, and add jitter to spread exchange requests.
- **FR-CYC-09** Public market data shall be fetched once per symbol, timeframe and candle, and shared by all agents that need it.

### 3.9 Risk Engine (RISK)

- **FR-RISK-01** The Risk Engine shall be deterministic and shall evaluate every order intent. AI output cannot bypass it.
- **FR-RISK-02** Per-agent risk settings, bounded by the global hard caps in configuration:

| Setting | Default | Notes |
|---|---|---|
| Max risk per trade | 1% of allocated capital | Loss incurred if the protective stop fills |
| Capital allocation | User-defined | Maximum quote amount committed |
| Max position notional | Equal to capital allocation | Additional cap |
| Max leverage (Futures) | 2x | Cannot exceed the global cap |
| Margin mode (Futures) | Isolated | Fixed in version 1 |
| Daily loss limit | 3% of allocated capital | Auto-pause until the next UTC day |
| Weekly loss limit | 6% of allocated capital | Auto-pause until the next UTC week |
| Consecutive losses before pause | 3 | 0 disables |
| Min reward-to-risk | 1.5 | Applies when a target is set |
| Max new entries per day | 4 | |
| Stop distance bounds | 0.5x to 4x ATR(14) of the agent timeframe | |
| Liquidation buffer (Futures) | 3x | Entry-to-liquidation distance ≥ 3 × entry-to-stop distance |
| Funding filter (Futures) | 0.05% per 8-hour equivalent | Skip entries when funding against the position exceeds the threshold |
| Cooldown after a loss | 1 cycle | |
| Protective stop | Always required | Not configurable |

- **FR-RISK-03** The Risk Engine shall compute position size as quantity = (allocated capital × risk %) ÷ |entry − stop|, then cap it by maximum notional, available balance or margin, and leverage, and round down to the exchange step size. If the result is below the exchange minimum, the intent is rejected.
- **FR-RISK-04** Orders shall satisfy the exchange filters for the symbol (tick size, step size, minimum and maximum quantity, minimum notional, price limits), using exchange information refreshed daily and after any filter-related rejection.
- **FR-RISK-05** The stop shall be on the correct side of the entry and within the configured ATR distance bounds.
- **FR-RISK-06** Stop adjustments shall only reduce risk (move toward breakeven or profit). Widening a stop is rejected.
- **FR-RISK-07** Adding to a position that is currently at a loss is rejected (no averaging down).
- **FR-RISK-08** When a daily or weekly loss limit or the consecutive-loss count is reached, the agent shall auto-pause for the applicable period and notify the user.
- **FR-RISK-09** For Futures, before entry the system shall set the symbol's leverage to the agent's value and the margin mode to isolated, and shall require the account to be in one-way position mode.
- **FR-RISK-10** Every evaluation shall record each rule's inputs, result and reason for the decision trace.
- **FR-RISK-11** No agent setting may exceed the global hard caps in configuration (maximum leverage, maximum risk per trade, minimum liquidation buffer, minimum reward-to-risk).

### 3.10 Order Execution and Position Management (EXE)

- **FR-EXE-01** Entries may be market or limit orders, as proposed. Unfilled limit orders shall be cancelled after a time-to-live expressed in candles.
- **FR-EXE-02** Immediately after an entry fills, the system shall place an exchange-resting protective stop (Futures: reduce-only stop-market; Spot: a stop-loss order, or a one-cancels-the-other order combining stop and target where supported). If the protective stop cannot be placed after retries, the system shall close the position at market and alert the user.
- **FR-EXE-03** Take-profit orders are optional and shall be reduce-only on Futures.
- **FR-EXE-04** Every order shall carry a unique client order ID derived from the agent, cycle and order leg, used for idempotency and attribution.
- **FR-EXE-05** On timeouts or server-side (5XX) errors, the order status shall be treated as unknown and queried by client order ID before any retry. Duplicate submissions shall never occur.
- **FR-EXE-06** The system shall reconcile at every cycle, and on a configurable interval for agents with open positions or orders: order statuses, fills, positions, and stop and target executions. Changes made outside Ecnanib (for example, the user closing the position on Binance) shall be detected, recorded as externally closed, and notified.
- **FR-EXE-07** The trade journal shall record for each trade: open and close times, side, quantity, average entry and exit prices, fees in quote currency (converted at fill time when charged in another asset), funding payments (Futures), realized P&L, R-multiple, maximum adverse and favorable excursion where computable, exit reason, and linked decision cycles.
- **FR-EXE-08** On startup, the system shall reconcile all active agents before resuming scheduled cycles.
- **FR-EXE-09** Exchange rate limits shall be respected per EXT-BIN-04. Protective actions (placing stops, closing positions) shall take priority over new entries and data requests.
- **FR-EXE-10** The system shall keep its clock offset to the exchange current for signed requests, and shall use a configurable receive window.

### 3.11 Trade Approval (APR)

- **FR-APR-01** In Confirm mode, every order intent that passes the Risk Engine shall become a pending approval that shows action, size, entry, stop, target, money at risk, the Strategist's rationale and the Risk Critic's verdict.
- **FR-APR-02** Users shall approve or reject in the app. Email notifications (when enabled) link to the approval page and require sign-in.
- **FR-APR-03** Approvals shall expire after a timeframe-specific window (configurable). On approval, the intent shall be re-validated against the current price and risk rules; if it is no longer valid, it is rejected automatically and the reason is shown.
- **FR-APR-04** Risk-reducing actions (tightening stops, closing positions when limits are hit) shall execute without approval when the agent's "auto-execute risk-reducing actions" option is on (default on).

### 3.12 AI Provider Trading Experience (EXP)

- **FR-EXP-01** The system shall attribute every decision and outcome to the provider and model that filled each role.
- **FR-EXP-02** The system shall compute metrics per provider, model and role, filterable by timeframe, market type, environment, symbol and period:
  - **Strategist:** proposals, executed trades, win rate, average R, expectancy, profit factor, total P&L, maximum drawdown, average holding time.
  - **Risk Critic:** approvals, modifications and rejections; outcomes of approved trades; shadow outcomes of rejected proposals ("veto accuracy").
  - **Analysts:** directional hit rate of their assessments over the next N candles.
  - **Operational:** latency (median and 95th percentile); error, timeout, refusal and contract-violation rates; tokens; cost; AI cost per trade; P&L net of AI cost.
- **FR-EXP-03** Proposals that were not executed but include entry, stop and target shall be simulated against subsequent candles to produce shadow outcomes, which shall always be labeled as hypothetical.
- **FR-EXP-04** After each trade closes, the Reviewer role shall write a structured reflection (Appendix B). The most recent N relevant reflections (per agent; default 5) shall be included in future Strategist and Risk Critic prompts.
- **FR-EXP-05** Users shall be able to view, edit, pin, exclude and delete reflections.
- **FR-EXP-06** A leaderboard view shall compare providers and models across the user's agents, show sample sizes, and warn when a sample is too small to be meaningful (for example, fewer than 30 trades).
- **FR-EXP-07** When enabled in configuration, the system may show a global leaderboard aggregated across users who opted in, displaying only groups that meet a minimum number of users, and no per-user data.
- **FR-EXP-08** Demo and Live results shall always be reported separately and never combined.

### 3.13 Notifications (NTF)

- **FR-NTF-01** In-app notifications shall be raised for: trade opened or closed; stop moved; approval pending or expired; agent auto-paused (loss limit, budget, errors); credential invalid; model deprecated or retired; kill switch executed; and, for administrators, catalog sync failures and exchange rate-limit bans.
- **FR-NTF-02** Email notifications shall be sent for user-selected events when email is enabled.
- **FR-NTF-03** Notifications shall contain no secrets and shall link to the relevant page.

### 3.14 Web UI (UI)

- **FR-UI-01** The UI shall provide: a landing page (with risk and non-affiliation notices); register, sign-in and password reset; dashboard; agent list; agent creation wizard; agent detail (overview, chart, council view, decision timeline, trades, reflections, settings); decision trace viewer; approvals; credentials; model catalog; trading experience; notifications; settings (profile, security, theme, animation); and administration.
- **FR-UI-02** The agent wizard shall follow these steps: Market and environment → Pair and timeframe → Roles and models → Risk settings → Execution mode and AI budget → Review and create. It shall explain Spot versus Futures, leverage, liquidation and funding in plain language.
- **FR-UI-03** The agent chart shall show candlesticks for the agent's timeframe, with markers for entries and exits, lines for the active stop and target, and markers for each decision cycle.
- **FR-UI-04** The decision trace viewer shall show, per cycle: an input snapshot summary, each role's rendered prompt and raw and parsed response, Risk Engine rule results, orders placed, and latency, tokens and cost. Secrets are never shown.
- **FR-UI-05** Agent status, cycle progress, prices, positions and P&L shall update live without manual refresh.
- **FR-UI-06** The layout shall be responsive for desktop, tablet and phone widths.
- **FR-UI-07** Demo agents and their results shall be visibly labeled "Demo" wherever they appear.

### 3.15 Animation (ANI)

- **FR-ANI-01** Council view: the roles of the current or last cycle shall be shown as animated cards or avatars with states (Idle, Thinking, Done, Failed, Skipped), animated hand-offs as outputs flow between roles, and a final reveal of the outcome (Executed, Held, Rejected by Risk Critic, Blocked by Risk Engine, Awaiting approval).
- **FR-ANI-02** Interface transitions (page changes, list items entering and leaving, expanding panels) shall be animated with short durations (about 150–400 ms) and easing.
- **FR-ANI-03** Numbers such as prices and P&L shall animate between values and briefly highlight on change. Direction shall also be shown with signs or icons, not by color alone.
- **FR-ANI-04** Charts shall animate smoothly when new candles or markers arrive.
- **FR-ANI-05** Loading states shall use skeleton placeholders where practical.
- **FR-ANI-06** Animations shall not gamify trading: no confetti, rewards or celebratory effects for opening trades or for profits.
- **FR-ANI-07** Animation shall follow the user's preference (FR-PREF-04): Reduced replaces motion with simple fades; Off disables non-essential animation.
- **FR-ANI-08** Animations shall never block interaction or delay the display of data, and shall remain smooth on mid-range devices.

### 3.16 Administration (ADM)

- **FR-ADM-01** Administrators shall be able to search and view users (email, status, created date, agent counts), disable and re-enable accounts, revoke sessions and delete accounts. They shall not see decrypted secrets, nor users' prompts, responses or custom instructions.
- **FR-ADM-02** Administrators shall manage prompt templates (FR-PRM-03).
- **FR-ADM-03** Administrators shall manage the model catalog (FR-CAT-03, FR-CAT-05) and the default role assignments.
- **FR-ADM-04** A system health page shall show: scheduler status, queued and running cycles, exchange request-weight usage and any active bans, provider error rates, recent errors, database migration version, application version and uptime, and credential re-encryption progress.
- **FR-ADM-05** A platform-wide emergency pause shall stop new entries for all agents while leaving protective orders in place. It requires confirmation and is audited.
- **FR-ADM-06** Administrators shall be able to search the audit trail.
- **FR-ADM-07** Administrators shall be able to view the effective configuration, with all secrets redacted.
- **FR-ADM-08** Administrators shall be able to publish a site-wide announcement banner (for example, planned maintenance).

### 3.17 Logging (LOG) and Audit (AUD)

- **FR-LOG-01** The system shall use a structured logging library comparable to Serilog: events use message templates with named properties, and the levels Verbose, Debug, Information, Warning, Error and Fatal.
- **FR-LOG-02** Sinks shall be configured in the configuration file: console; rolling file (by day or size, with a retained-file count); and a PostgreSQL table (optional, with its own minimum level and retention).
- **FR-LOG-03** Minimum levels shall be configurable globally and per category; level changes shall take effect without restart.
- **FR-LOG-04** Events shall be enriched, where applicable, with: UTC timestamp, level, category, correlation ID, user ID, agent ID, cycle ID, environment (Demo/Live), application version and machine name.
- **FR-LOG-05** Masking and destructuring policies shall prevent secrets, API keys, passwords, TOTP codes, tokens, signatures and authorization headers from being written to any sink. Automated tests shall verify this.
- **FR-LOG-06** HTTP request logging shall record method, route, status and duration, without bodies for authentication and credential endpoints.
- **FR-LOG-07** Each AI call shall be logged with provider, model, role, latency, tokens, cost and outcome. Full prompts and responses are stored in decision traces, not in general logs.
- **FR-LOG-08** Each exchange call shall be logged with endpoint, request weight used, status, latency and client order ID — never with keys or signatures.
- **FR-AUD-01** An audit trail, stored in PostgreSQL separately from logs and append-only from the application's perspective, shall record: sign-in success and failure; two-factor changes; password changes; credential add, validate, rotate and delete; agent create, update, activate, pause, stop, archive and delete; switches to Live; execution-mode changes; kill switches; approvals and rejections; emergency pauses; administrator actions; and master-key rotation.
- **FR-AUD-02** Each audit record shall contain timestamp, actor (user, administrator or system), action, target, a before/after summary without secrets, IP address, user agent and correlation ID.
- **FR-AUD-03** Users shall be able to view their own audit history.

### 3.18 Data Retention and Privacy (DATA)

- **FR-DATA-01** Retention periods shall be configurable, with these defaults: decision traces 180 days; database log events 30 days; audit records 730 days; cached candles 7 days. Trades, journal entries and reflections are kept until the user deletes them or their account.
- **FR-DATA-02** A daily job shall delete expired rows in batches (no partitioning).
- **FR-DATA-03** Users shall be able to export their trades, decision summaries, reflections and metrics as CSV and JSON.
- **FR-DATA-04** Account deletion follows FR-AUTH-09.

---

## 4. External Interface Requirements

### 4.1 User Interface

- **EXT-UI-01** The browser-based UI shall meet WCAG 2.1 AA in both the Dark and Light themes.
- **EXT-UI-02** The UI shall be in English in version 1, with all user-facing text externalized for future localization.
- **EXT-UI-03** Mandatory notices — risk disclosure, "not financial advice", and "not affiliated with or endorsed by Binance" — shall be shown on the landing page, at registration, and when switching an agent to Live.

### 4.2 Binance

- **EXT-BIN-01** The system shall integrate with the Binance Spot REST API and the USDⓈ-M Futures REST API. Live and Demo base URLs for each market shall come from the configuration file.
- **EXT-BIN-02** Public market data may be requested from the exchange's market-data-only endpoint.
- **EXT-BIN-03** Signed requests shall include a timestamp and receive window, with server time synchronization (FR-EXE-10).
- **EXT-BIN-04** Request-weight limits apply per IP address, so all users share the server's limit. The system shall track used weight from response headers, keep configurable headroom, back off on HTTP 429, and halt all exchange requests on HTTP 418 until the ban expires. Order-count limits apply per exchange account and shall also be tracked.
- **EXT-BIN-05** Responses with server-side (5XX) errors shall be treated as unknown outcomes (FR-EXE-05).
- **EXT-BIN-06** Exchange information (symbols, filters) shall be cached and refreshed daily.
- **EXT-BIN-07** Futures prerequisites shall be validated before activation: futures enabled on the key, one-way position mode, and sufficient margin balance.
- **EXT-BIN-08** Demo prices and order books resemble, but do not equal, live markets. Demo results shall be labeled accordingly.

### 4.3 AI Providers

- **EXT-AI-01** The system shall integrate with the public APIs of Anthropic, Google Gemini, OpenAI and xAI using user credentials.
- **EXT-AI-02** Capabilities used where supported: text generation, structured (schema-constrained) output, image input (rendered charts), provider-native web or X search tools, token-usage reporting and model listing.
- **EXT-AI-03** Per provider, the configuration shall define base URL, request timeout, maximum retries and backoff, and maximum concurrent requests per user credential.
- **EXT-AI-04** Provider errors shall be normalized into authentication, rate limit, overloaded, invalid request, refusal, timeout and other, for metrics and fallback logic.
- **EXT-AI-05** Provider adapters shall share a common interface so that council logic is provider-agnostic.

### 4.4 Email (Optional)

- **EXT-EML-01** SMTP settings shall come from the configuration file. When email is disabled, verification is skipped (Live activation then does not require it) except for administrators (FR-AUTH-10), password resets are issued by administrators, and only in-app notifications are sent.

### 4.5 Database

- **EXT-DB-01** The system shall use a single PostgreSQL database, accessed through a code-first ORM.
- **EXT-DB-02** Migrations shall be applied automatically at startup (NFR-DEP-03). No table partitioning shall be used.

---

## 5. Non-Functional Requirements

### 5.1 Security (SEC)

- **NFR-SEC-01** All traffic shall use HTTPS with HSTS. TLS may terminate at a reverse proxy.
- **NFR-SEC-02** Passwords shall be hashed with a salted, memory-hard algorithm (e.g., Argon2id), with a minimum length of 12 characters.
- **NFR-SEC-03** The application shall mitigate the OWASP Top 10, including injection, cross-site scripting, cross-site request forgery, server-side request forgery and broken access control.
- **NFR-SEC-04** Every data access shall be scoped to the owning user. Automated tests shall verify that no user can read or modify another user's data.
- **NFR-SEC-05** Secrets shall be encrypted per FR-KEY-07. The configuration file shall be readable only by the application's service account. The application shall refuse to start if no valid active master key is configured, or if the key equals the sample placeholder.
- **NFR-SEC-06** Session cookies shall be HttpOnly, Secure and SameSite. Responses shall include security headers (content security policy, frame protection, referrer policy).
- **NFR-SEC-07** Authentication and API endpoints shall be rate-limited per IP address and per account.
- **NFR-SEC-08** Prompt-injection resilience: external content is treated as data; model output is never executed as code or used to call arbitrary endpoints; only schema-validated fields are used; and the Risk Engine never trusts model output.
- **NFR-SEC-09** Dependencies shall be scanned for known vulnerabilities as part of the build process.
- **NFR-SEC-10** External endpoints (exchange and AI provider base URLs) shall come only from the configuration file. Users cannot supply endpoints.

### 5.2 Reliability (REL)

- **NFR-REL-01** Target availability is 99.5% per month, excluding planned maintenance.
- **NFR-REL-02** Open positions shall stay protected by exchange-resting stops regardless of system availability (FR-EXE-02).
- **NFR-REL-03** Each order leg shall be submitted at most once (FR-EXE-04, FR-EXE-05).
- **NFR-REL-04** Graceful shutdown shall complete in-flight exchange calls and persist state; no trade shall be left half-recorded.
- **NFR-REL-05** A failure of one agent, user credential or provider shall not affect other agents.
- **NFR-REL-06** The operator shall run daily database backups; point-in-time recovery is recommended.

### 5.3 Performance and Capacity (PERF)

- **NFR-PERF-01** For at least 99% of cycles, the cycle shall start within 30 seconds after the candle close plus the configured delay.
- **NFR-PERF-02** Default cycle deadlines: 15m — 3 minutes; 1H — 10 minutes; 4H — 20 minutes; 1D — 30 minutes.
- **NFR-PERF-03** Page data requests that do not call external APIs shall respond within 500 ms at the 95th percentile. Pages shall become interactive within 3 seconds on a typical broadband connection.
- **NFR-PERF-04** A single instance shall support at least 200 concurrently active agents across all timeframes while keeping exchange request weight below the configured headroom. The design shall document its request-weight model so capacity can be estimated.
- **NFR-PERF-05** Exchange request volume shall be minimized through shared market data (FR-CYC-09), symbol-scoped queries instead of account-wide snapshots where possible, and scheduling jitter.

### 5.4 Usability and Accessibility (USA)

- **NFR-USA-01** The UI shall meet WCAG 2.1 AA, including keyboard navigation and screen-reader labels.
- **NFR-USA-02** A first-time user shall be able to register, add credentials and start a Demo agent within 10 minutes without documentation (validated by usability testing).
- **NFR-USA-03** Every risk setting shall have inline help in plain language.
- **NFR-USA-04** Charts, code and trace views shall follow the active theme.

### 5.5 Maintainability (MNT)

- **NFR-MNT-01** Module boundaries shall follow Section 2.2, and modules shall interact through defined internal interfaces.
- **NFR-MNT-02** Adding an AI provider shall require only a new provider adapter and configuration, with no changes to council, risk or execution logic.
- **NFR-MNT-03** Exchange access shall sit behind an adapter interface.
- **NFR-MNT-04** Automated tests shall cover every Risk Engine rule, position-sizing math, exchange-filter rounding, output-contract validation, secret masking, user data isolation and order idempotency.
- **NFR-MNT-05** Migrations shall be forward-only and shall be tested against a copy of the production schema before release.

### 5.6 Observability (OBS)

- **NFR-OBS-01** A health endpoint shall report liveness and readiness, including database connectivity and migration status.
- **NFR-OBS-02** A correlation ID shall link each request or cycle to its AI calls, exchange calls, log events and audit records.

### 5.7 Legal and Compliance (LEG)

- **NFR-LEG-01** Accepted versions of the Terms of Use and Risk Disclosure shall be recorded, and users shall re-accept after material changes.
- **NFR-LEG-02** The notices in EXT-UI-03 shall be shown.
- **NFR-LEG-03** The system shall support data export and account deletion, and shall collect only the data needed to operate.
- **NFR-LEG-04** The operator may configure a list of blocked countries, checked against the country the user declares at registration.
- **NFR-LEG-05** The compliance items in ASM-05 shall be resolved before public launch.

### 5.8 Deployment (DEP)

- **NFR-DEP-01** A deployment consists of the application, its configuration file and a PostgreSQL database.
- **NFR-DEP-02** The first start against an empty database shall create the schema and seed reference data: providers, prompt templates and default role assignments.
- **NFR-DEP-03** On upgrade, replacing the application and restarting shall apply pending migrations automatically. If a migration fails, the application shall not start, and the failure shall be logged.
- **NFR-DEP-04** The application shall read its configuration file from a fixed location relative to the application and shall not read environment variables.

---

## 6. Data Requirements

### 6.1 Conceptual Entities

| Entity | Key attributes |
|---|---|
| User | Email, password hash, email verified, failed sign-ins, locked until, encrypted TOTP secret, status, created. Administrator rights are worked out from the configuration file (FR-AUTH-10), not stored |
| LegalAcceptance | User, document, version, accepted at |
| UserPreference | Theme, animation level, timezone, notification settings |
| Session | User, created, last seen, expires, IP address, user agent |
| Credential | User, provider, environment, label, ciphertext, encryption key ID, last four, status, permissions, last validated, last used |
| Provider | Name, enabled |
| Model | Provider, model ID, display name, capabilities, context window, pricing, status, first and last seen, overrides |
| ModelAvailability | Credential, model, available, checked at |
| CatalogSyncRun | Provider, trigger, start, end, counts, errors |
| Agent | User, name, market, environment, symbol, timeframe, execution mode, status, capital allocation, AI budget, custom instructions |
| AgentRoleAssignment | Agent, role, model, fallback model |
| AgentRiskSettings | Agent, settings per FR-RISK-02 |
| PromptTemplate | Layer, role, market, timeframe, version, content, status, author, published at |
| DecisionCycle | Agent, scheduled at, started, finished, status, outcome, input snapshot, total tokens and cost |
| AiCall | Cycle, role, model, template versions, rendered prompt, raw response, parsed output, tokens, cost, latency, status, error type |
| RiskEvaluation | Cycle, inputs, per-rule results, verdict, computed quantity |
| Approval | Cycle, status, expires at, decided by, decided at, re-validation result |
| Order | Agent, cycle, client order ID, exchange order ID, type, side, price, stop price, quantity, status, timestamps |
| Fill | Order, price, quantity, fee, fee asset, time |
| Trade | Agent, side, quantity, average entry and exit, open and close times, fees, funding, realized P&L, R-multiple, excursions, exit reason |
| FundingPayment | Trade, time, rate, amount |
| Reflection | Agent, trade, model, structured content, pinned, excluded |
| ShadowOutcome | Cycle, proposal, simulated result |
| PerformanceSnapshot | Provider, model, role, dimensions, period, metrics |
| ExchangeSymbol | Market, symbol, filters, refreshed at |
| CandleCache | Market, symbol, timeframe, open time, OHLCV |
| Notification | User, type, payload, read at |
| AuditEvent | Timestamp, actor, action, target, summary, IP address, user agent, correlation ID |
| LogEvent | Written by the database log sink when enabled |
| JobRun | Job name, start, end, status, details |

### 6.2 Data Rules

- **DATA-R-01** All timestamps are stored in UTC.
- **DATA-R-02** Prices, quantities and amounts of money are stored as exact decimal values, never binary floating point, together with the asset or currency code.
- **DATA-R-03** No table partitioning. Time-range queries are supported by indexes, and retention is enforced by batched deletes (FR-DATA-02).
- **DATA-R-04** Agents are archived (soft-deleted) by default. Account deletion hard-deletes the user's data, except anonymized audit records.
- **DATA-R-05** The schema is owned by the application's code-first model; manual schema changes are not permitted.

---

## 7. Configuration File

### 7.1 Rules

- **CFG-01** All configuration is read from a single appsettings-style JSON file (referred to here as `appsettings.json`). There are no environment variables, no external secret vault and no other configuration sources.
- **CFG-02** The file contains secrets. It shall be excluded from source control, readable only by the service account, and covered by backups that are themselves protected. An `appsettings.example.json` with placeholders shall ship with the product.
- **CFG-03** The file shall be validated at startup. Missing or invalid values shall stop startup with a clear message naming the setting.
- **CFG-04** Changes to the `Logging` section shall apply without restart; all other changes take effect after restart.
- **CFG-05** The effective configuration shown to administrators shall redact all secrets (FR-ADM-07).

### 7.2 Sections

| Section | Purpose |
|---|---|
| `App` | Public base URL; server egress IP addresses (shown to users for key whitelisting) |
| `Database` | PostgreSQL connection string, auto-migrate flag, command timeout |
| `Security` | Encryption master keys (ID, key, active flag), session signing key, session timeouts, password policy, lockout, two-factor requirement for Live, credential revalidation interval |
| `Admin` | Administrator email addresses; bootstrap secret, required when an address is listed and email is disabled (FR-AUTH-10) |
| `Email` | Enabled flag and SMTP settings |
| `Legal` | Blocked countries as ISO 3166-1 alpha-2 codes (NFR-LEG-04) |
| `Logging` | Minimum level, per-category overrides, sinks (console, rolling file, database) and their options |
| `Binance` | Live and Demo base URLs per market, market-data URL, receive window, request-weight headroom, exchange-info refresh, reconciliation interval |
| `AiProviders` | Per provider: enabled, base URL, timeout, retries, concurrency per credential |
| `Catalog` | Sync interval, retirement grace period, pricing and capability overrides |
| `RoleDefaults` | Default provider per role |
| `Agents` | Maximum agents per user, default environment, candle-close delay, maximum concurrent cycles, catch-up grace windows, cycle deadlines, approval windows |
| `RiskCaps` | Global hard caps |
| `Experience` | Default reflections in prompts, shadow tracking, global leaderboard settings |
| `Retention` | Retention periods |
| `Ui` | Default theme and animation level |

### 7.3 Example (Placeholders Only)

```json
{
  "App": {
    "PublicBaseUrl": "https://ecnanib.example.com",
    "EgressIpAddresses": [ "203.0.113.10" ]
  },
  "Database": {
    "ConnectionString": "<PostgreSQL connection string>",
    "AutoMigrate": true,
    "CommandTimeoutSeconds": 30
  },
  "Security": {
    "EncryptionKeys": [
      { "Id": "2026-11", "Key": "<base64-encoded 256-bit key>", "Active": true }
    ],
    "SessionSigningKey": "<long random secret>",
    "SessionIdleTimeoutMinutes": 60,
    "SessionAbsoluteTimeoutHours": 24,
    "PasswordMinLength": 12,
    "MaxFailedSignIns": 5,
    "LockoutMinutes": 15,
    "RequireTwoFactorForLive": false,
    "CredentialRevalidationHours": 24
  },
  "Admin": {
    "AdminEmails": [ "admin@example.com" ],
    "BootstrapSecret": "<random secret of at least 32 characters>"
  },
  "Legal": {
    "BlockedCountries": []
  },
  "Email": {
    "Enabled": false,
    "SmtpHost": "<smtp host>",
    "SmtpPort": 587,
    "UseTls": true,
    "Username": "<smtp user>",
    "Password": "<smtp password>",
    "FromAddress": "no-reply@example.com"
  },
  "Logging": {
    "MinimumLevel": "Information",
    "Overrides": {
      "Exchange": "Information",
      "AiCouncil": "Information",
      "Http": "Warning"
    },
    "Sinks": {
      "Console": { "Enabled": true },
      "File": {
        "Enabled": true,
        "Path": "logs/ecnanib-.log",
        "RollingInterval": "Day",
        "RetainedFileCount": 14
      },
      "Database": {
        "Enabled": true,
        "MinimumLevel": "Warning"
      }
    }
  },
  "Binance": {
    "Spot": {
      "LiveBaseUrl": "https://api.binance.com",
      "DemoBaseUrl": "https://demo-api.binance.com"
    },
    "Futures": {
      "LiveBaseUrl": "https://fapi.binance.com",
      "DemoBaseUrl": "https://demo-fapi.binance.com"
    },
    "MarketDataBaseUrl": "https://data-api.binance.vision",
    "RecvWindowMs": 5000,
    "WeightHeadroomPercent": 30,
    "ExchangeInfoRefreshHours": 24,
    "ReconcileIntervalSeconds": 60
  },
  "AiProviders": {
    "Anthropic": { "Enabled": true, "BaseUrl": "<API base URL>", "TimeoutSeconds": 120, "MaxRetries": 2, "MaxConcurrentPerCredential": 4 },
    "Google": { "Enabled": true, "BaseUrl": "<API base URL>", "TimeoutSeconds": 120, "MaxRetries": 2, "MaxConcurrentPerCredential": 4 },
    "OpenAI": { "Enabled": true, "BaseUrl": "<API base URL>", "TimeoutSeconds": 120, "MaxRetries": 2, "MaxConcurrentPerCredential": 4 },
    "xAI": { "Enabled": true, "BaseUrl": "<API base URL>", "TimeoutSeconds": 120, "MaxRetries": 2, "MaxConcurrentPerCredential": 4 }
  },
  "Catalog": {
    "SyncIntervalHours": 12,
    "RetireAfterMissingDays": 7,
    "Overrides": []
  },
  "RoleDefaults": {
    "TechnicalAnalyst": "Google",
    "SentimentAnalyst": "xAI",
    "Strategist": "OpenAI",
    "RiskCritic": "Anthropic",
    "Reviewer": "Google"
  },
  "Agents": {
    "MaxAgentsPerUser": 10,
    "DefaultEnvironment": "Demo",
    "CandleCloseDelaySeconds": 10,
    "MaxConcurrentCycles": 50,
    "CatchUpGraceMinutes": { "15m": 5, "1H": 15, "4H": 60, "1D": 180 },
    "CycleDeadlineMinutes": { "15m": 3, "1H": 10, "4H": 20, "1D": 30 },
    "ApprovalWindowMinutes": { "15m": 5, "1H": 20, "4H": 60, "1D": 240 }
  },
  "RiskCaps": {
    "MaxLeverage": 5,
    "MaxRiskPerTradePercent": 2,
    "MinLiquidationBufferMultiple": 2,
    "MinRewardToRisk": 1.0
  },
  "Experience": {
    "DefaultReflectionsInPrompt": 5,
    "ShadowTracking": true,
    "GlobalLeaderboard": { "Enabled": false, "MinimumUsersPerGroup": 10 }
  },
  "Retention": {
    "DecisionTraceDays": 180,
    "DatabaseLogDays": 30,
    "AuditDays": 730,
    "CandleCacheDays": 7
  },
  "Ui": {
    "DefaultTheme": "System",
    "DefaultAnimation": "System"
  }
}
```

---

## 8. Key Use Cases

**UC-01 Start trading in Demo** — *Actor: User*
1. The user registers, accepts the Terms of Use and Risk Disclosure, and verifies their email.
2. The user adds a key for at least one AI provider and a Binance Demo key; the system validates both and syncs the model catalog.
3. The user completes the agent wizard (for example: Futures, Demo, BTCUSDT, 1H, suggested role assignments, default risk settings, Autonomous).
4. The system validates the prerequisites, activates the agent and shows the next cycle time.

**UC-02 Scheduled decision cycle** — *Actor: System*
- Main flow: as in FR-CYC-02, ending in HOLD, an executed trade, or a pending approval.
- Alternate flows:
  - A1 — The Sentiment Analyst's provider times out: the fallback model is used; if it also fails, the role is skipped and the cycle continues.
  - A2 — The Strategist's output is invalid twice: the cycle outcome is HOLD, and the trace records the contract violation.
  - A3 — The Risk Engine rejects the intent (for example, insufficient liquidation buffer): no order is sent, and the reasons are recorded in the trace.
  - A4 — The protective stop cannot be placed: the position is closed at market and the user is alerted.
  - A5 — The exchange returns HTTP 429: requests back off, and protective actions take priority.

**UC-03 Approve a trade** — *Actor: User (Confirm mode)*
1. The user receives a pending-approval notification.
2. The user reviews the proposal, the Risk Critic's verdict and the money at risk.
3. The user approves; the system re-validates and executes, or rejects the intent as stale.

**UC-04 Switch an agent to Live** — *Actor: User*
1. On a paused or stopped agent with no open positions or orders, the user selects "Switch to Live".
2. The system requires a valid Live Binance credential, a verified email (when email is enabled), two-factor authentication (when required), and acknowledgement of the Risk Disclosure and execution mode.
3. The agent becomes Active in Live, and an audit record is written.

**UC-05 Emergency stop** — *Actor: User*
1. The user selects "Stop all agents" and confirms (with two-factor authentication when enabled).
2. The system cancels the agents' open orders, closes their positions at market, stops the agents and reports the result per agent.

**UC-06 Compare providers** — *Actor: User*
1. The user opens Trading Experience and filters by Live, Futures, 4H and the last 90 days.
2. The system shows per-role metrics with sample-size warnings.
3. The user applies "Suggest assignment" to an agent.

**UC-07 Model retired by a provider** — *Actor: System*
1. A catalog sync no longer returns a model; the model is marked Deprecated, and affected users are warned.
2. After the grace period, the model is marked Retired; affected roles switch to their fallbacks or the agents pause, and users are notified.

**UC-08 Publish a prompt version** — *Actor: Administrator*
1. The administrator edits the Futures × 15m overlay, previews it with sample data and publishes it as a new version.
2. New cycles use the new version and record it in their traces; the administrator can roll back.

---

## 9. Acceptance Criteria

- **AC-01** No order is ever sent to the exchange without a corresponding passing Risk Engine evaluation (verified by tests and by an audit query over traces and orders).
- **AC-02** Every filled entry has an exchange-resting protective stop within 10 seconds, or the position is closed (fault-injection test).
- **AC-03** Logs, traces, exports and notifications contain no secret values (automated scan with seeded canary secrets).
- **AC-04** Binance keys with withdrawal permission are rejected.
- **AC-05** The default theme is System, it follows operating-system changes live, and the wrong theme never flashes on load.
- **AC-06** With reduced motion enabled in the operating system, no non-essential motion animation plays.
- **AC-07** A fresh install on an empty database creates the schema and seed data; an upgrade applies migrations automatically; a failing migration prevents startup.
- **AC-08** The application runs with exactly one configuration file and no environment variables.
- **AC-09** Catalog sync adds new models, and models missing beyond the grace period become Retired, with fallback handling.
- **AC-10** Cycles for all four timeframes start within the NFR-PERF-01 tolerance under the NFR-PERF-04 load.
- **AC-11** Cross-user access attempts on every endpoint are denied (automated test suite).
- **AC-12** Demo and Live metrics are never combined in any view or aggregate.
- **AC-13** Two agents cannot be active on the same credential, market and symbol.
- **AC-14** No duplicate order is submitted when the exchange returns timeouts or 5XX errors (fault-injection test).

---

## 10. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| AI trading decisions are unreliable; public tests of LLM trading have shown large losses and unstable rankings | Deterministic Risk Engine, Demo by default, Confirm mode, loss limits, full traces, sample-size warnings |
| All users share the server IP's exchange rate limit | Shared market data, weight tracking with headroom, jitter, priority for protective actions, documented capacity model |
| Master keys live in the configuration file (no vault or environment variables, by requirement) | Strict file permissions, exclusion from source control, protected backups, key rotation, refusal of placeholder keys; residual risk accepted by the stakeholder |
| Prompt injection through news or social content | Delimited untrusted content, schema-only outputs, no execution of model output, independent Risk Engine |
| Users' AI costs grow unexpectedly | Per-agent AI budget, per-call cost tracking, protective-only mode when the budget is exhausted |
| Providers change or retire models frequently | Catalog sync, deprecation warnings, fallbacks, automatic pause |
| Exchange outages or API changes | Exchange-resting stops, reconciliation on startup and on an interval, adapter isolation |
| Regulatory, provider-policy and naming exposure (including the similarity of "Ecnanib" to "Binance") | Legal review before launch (ASM-05), disclaimers, non-affiliation notice, no Binance trade dress |
| Users over-trust short or Demo track records | Hypothetical labeling, Demo/Live separation, minimum-sample warnings |

---

## 11. Open Questions

1. Should a future version support several symbols per agent?
2. Should the Futures hedge (dual-side) position mode be supported?
3. Should the global leaderboard launch enabled, and should participation be opt-in by default?
4. What are the final retention periods for decision traces and audit records?
5. Should administrators be able to view a user's decision traces for support, with explicit user consent?
6. Will email be available at launch (this affects verification, password reset and notifications)?
7. What are the final values for maximum agents per user and platform capacity?
8. Which jurisdictions, if any, should be blocked at registration?
9. Has the product name been cleared legally?

---

## Appendix A — Prompt Overlay Requirements

### A.1 Timeframe Overlays

| Overlay | Higher-timeframe context | Typical holding period | Content the overlay shall cover |
|---|---|---|---|
| 15m | 1H trend | Minutes to hours | Trade only in the direction of the 1H trend. Fees and spread are large relative to moves, so skip low reward-to-risk setups and prefer limit entries. Avoid entries around major scheduled economic releases. |
| 1H | Daily trend | Hours to days | Trade in the direction of the daily trend. Account for overnight and weekend exposure. Prefer fewer, higher-quality setups. |
| 4H | Daily trend (e.g., 200-day average) | Days to weeks | Swing setups. Use time-based exits when a trade stalls. Treat correlated assets as one bet. On Futures, account for funding paid over multi-day holds. |
| 1D | Weekly trend and 200-day average | Weeks to months | Trend-following and breakout logic. Expect few signals and a low win rate offset by large winners. Use wide stops (around 3× ATR) and trailing exits. On Futures, funding is a major cost, and long exposure may be cheaper on Spot. |

### A.2 Market Overlays

| Topic | Spot | USDⓈ-M Futures |
|---|---|---|
| Direction | Long only; can only sell what the agent holds | Long or short |
| Leverage | None | Up to the agent's cap; isolated margin |
| Protective orders | Stop-loss, or one-cancels-the-other (stop + target) | Reduce-only stop-market; the liquidation price must stay well beyond the stop |
| Costs | Trading fees | Trading fees and funding payments |
| Special risks | Holding through downtrends; staying in cash is a valid position | Liquidation, funding, short squeezes, crowded positioning (extreme funding rates) |
| Allowed actions | FR-CYC-03, Spot column | FR-CYC-03, Futures column |

---

## Appendix B — Role Output Contracts

Every role returns a structured object validated against a schema. Required fields:

| Contract | Fields |
|---|---|
| Technical assessment | Trend (up / down / range); HTF trend; key support and resistance levels; signals (name, direction, strength); volatility regime; setup present (yes / no); confidence (0–1); summary (≤ 600 characters) |
| Sentiment assessment | Score (−1 to +1); main drivers; sources (title, URL, published time); data freshness; confidence (0–1); summary |
| Trade proposal | Action (FR-CYC-03); side; entry type (market / limit); entry price; stop price; target prices; requested risk percent (≤ the agent's maximum); limit-order time-to-live in candles; invalidation condition; rationale; confidence (0–1) |
| Risk verdict | Verdict (approve / modify / reject); modifications (stop, target, risk percent — risk-reducing only); concerns; checklist results (trend alignment, reward-to-risk, event risk, funding, consistency with reflections); rationale |
| Reflection | Setup summary; outcome (R-multiple, P&L); what worked; what failed; lesson (one sentence); tags |