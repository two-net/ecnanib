# Accounts module

Registration, sessions, password change, account deletion, fair-use limits and the risk disclaimer (SRS §3.2
FR-ACC-01..08; NFR-SEC-01/03; NFR-LEGAL-01/02; C-12, A-09). Other modules depend on `Ecnanib.Accounts.Abstractions`
(`IAccountService`, `IFairUseLimits`, `IUserPreferencesResolver`, the user-id extensions), `AddAccounts(...)` and
`MapAccountEndpoints()`; the hashing, cookie and throttle types never leave this folder, whose own pages under
`Components/Account` are part of it and issue and end the cookie session through `AccountSession` (NFR-ARCH-02). There
are users and nothing else: no email verification, roles, payments, plans, organizations or tenants (FR-ACC-05/06).
`AddAccounts` requires `AddDatabase`, `AddServer` and `AddUi`.

| Need | Use |
|---|---|
| The signed-in user's id for any data access (NFR-SEC-03) | `HttpContext.User.RequireUserId()` (or `GetUserId()` when anonymous is possible); nothing else identifies a user |
| Register, authenticate, change password, delete (FR-ACC-01..04) | `IAccountService` → `RegistrationResult`, `AuthenticationResult`, `PasswordChangeResult`, `AccountDeletionResult`; user mistakes are results, exceptions mean the database failed |
| Start or end the cookie session after a result (FR-ACC-02) | `AccountSession.SignInAsync(httpContext, user)` from a static page handler; the layout's `POST /sign-out` form, which calls `IAccountService.SignOutAsync` (every session of the user ends) and deletes the cookie |
| Require a signed-in user on a page | `@attribute [Authorize]`; the middleware challenges with a redirect to `/sign-in?ReturnUrl=…` |
| Agent limits at creation and start (FR-ACC-07, FR-AGT-15) | `IFairUseLimits.CheckCreateAgentAsync` / `CheckStartAgentAsync` → `FairUseCheck` (allowed, current, max) |
| Disclaimer text, version and status (FR-ACC-08, FR-UI-05, NFR-LEGAL-01/02) | `RiskDisclaimer` (the paragraphs, the version, the footer's one-sentence `Summary` and the acceptance texts); `<RiskDisclaimerNotice />` for the text alone, or with `Status` and `ReturnTo` for the text with the acceptance form (one such instance per page); `IAccountService.GetDisclaimerStatusAsync(...).IsAccepted`, which a live-mode start must require |
| The user's theme and animation preference (FR-UI-02, FR-UI-03) | `IAccountService.GetPreferencesAsync` / `UpdatePreferencesAsync` → `UserPreferences`; `IUserPreferencesResolver.ResolveAsync(httpContext, ct)` for the preference a page renders with (the user's own, or the `Ui` defaults for a visitor and on error pages) |

Rules the module enforces:

- Passwords are hashed with Argon2id (64 MiB, 3 iterations, 1 lane, 16-byte salt) in PHC form; a hash made with weaker
  parameters is upgraded at the user's next sign-in. Length is the only password rule: `Accounts:MinPasswordLength` to
  128 characters (NFR-SEC-01). Emails are trimmed and matched case-insensitively through `users.normalized_email`.
- One persistent, sliding cookie session (`ecnanib.session`: HttpOnly, SameSite=Lax, Secure when `Server:PublicUrl` is
  https, lifetime `Accounts:SessionDays`), protected by the Data Protection key ring the database module persists
  (FR-CFG-05) under the application name `Ecnanib`. There is no session before authentication (no session state, no
  pre-auth cookie), and every sign-in mints a fresh ticket from the account alone, so a cookie planted in the browser
  beforehand is replaced, never adopted (fixation, NFR-SEC-01). Every state-changing POST, sign-out included, carries an
  antiforgery token; the antiforgery cookie (`ecnanib.antiforgery`: HttpOnly, SameSite=Strict, Secure by the same rule)
  is configured beside the session cookie, and the frame headers are the pipeline's (configuration module).
- The ticket carries the user's security stamp. It is compared with the account every `Accounts:SessionRevalidationMinutes`,
  and at once when the user signed out, changed their password or deleted the account (an in-process revocation list;
  after a restart the interval alone bounds the gap). Sign-out rotates the stamp, so it ends every session of the user
  on every device (the layout's button says so) and a copied cookie stops working; the cookie is deleted even when the
  database refused the rotation.
  A password change ends every other session and re-issues the current one (FR-ACC-03); deletion ends them all.
- Sign-in is throttled in memory (one instance, C-10): `Accounts:SignInAttemptsPerWindow` failures per (email, client
  address) pair and `Accounts:SignInAttemptsPerAddress` failed sign-ins plus registrations per address, each within
  `Accounts:SignInWindowMinutes`. An attempt is counted before the password is checked and refunded when it succeeds or is
  refused, so a burst of parallel attempts is cut at the limit and only failures stay counted. Keying the credential
  bucket by address too keeps an attacker from locking a user out of their own kill switch (FR-EXE-10). Wrong current
  passwords on the settings pages count as well. An unknown email costs the same as a wrong password and gets the same
  message. Behind a reverse proxy the address is the real client's once the proxy is trusted (`Server:TrustedProxies`,
  loopback by default; configuration module).
- Account deletion needs the password again (the explicit confirmation of FR-ACC-04) and every agent Stopped or Draft.
  One transaction reads the user's agent ids, deletes the allowed agents, counts what remains, then removes the log
  entries that carry the user id or one of those agent ids (no foreign key, FR-LOG-08; a row written for an agent
  outside a scope carries no user id, NFR-SEC-03) and the user row, from which keys, agents, decisions, trades and
  acceptances cascade. An agent started concurrently either blocks the deletion or finds its row gone.
- Fair-use limits come from the `Agents` section and are identical for every user (FR-ACC-07); Running and Paused agents
  count as running, an agent in Error does not.
- A new account takes the `Ui` section's theme and animation defaults (`users.theme`, `users.reduced_motion`); the
  settings page changes them per user, and the root component reads them with one primary-key query per request so the
  page is themed before the first paint. A visitor, an error page and a database failure all render with the defaults.
- The disclaimer is versioned (`RiskDisclaimer.Version`): one acceptance row per version, the first timestamp stands,
  `users.disclaimer_accepted_at` mirrors the latest. Changing the text means bumping the version, which asks everyone
  again; `RiskDisclaimerTests` pins a hash of the paragraphs beside the version, so a changed paragraph under the same
  version fails the build. The footer's `Summary` is not part of what the user accepts and is outside that rule.
- Authentication events are logged with the user id only (FR-LOG-04): registration, sign-in, sign-out, failures,
  throttling, password change, acceptance, deletion and rejected sessions. No submitted email or password ever reaches a
  log or an exception message (FR-LOG-05); the unique-index violation on a registration race is not logged because its
  text names the email.

Configuration (the `Accounts` section goes beyond Appendix D's minimum; the agents issue extends `Agents`):

```json
{ "Accounts": { "SessionDays": 30, "SessionRevalidationMinutes": 5, "SignInAttemptsPerWindow": 10, "SignInAttemptsPerAddress": 100, "SignInWindowMinutes": 15, "MinPasswordLength": 8 },
  "Agents": { "MaxAgentsPerUser": 10, "MaxRunningAgentsPerUser": 3 } }
```

Pages (static server rendering, plain forms): `/sign-up`, `/sign-in` (honours a local `ReturnUrl` only), `/settings`
(appearance: theme and reduced animation, saved without a reload by the `PreferencesPanel` island; password; disclaimer
status and acceptance; link to deletion), `/settings/delete-account`, and `POST /sign-out` from the layout. The landing
page shows the disclaimer (FR-UI-05). The acceptance form is `RiskDisclaimerNotice` given a `Status`: the settings page
shows it until the current version is accepted, and the agents module shows it where Start is offered, on a live agent's
page and on its review step while the agent can be started (Draft, Stopped or Error; a Paused agent's Resume is not a
start, FR-ACC-08); the post records the acceptance and redirects to the page's `ReturnTo` with
`status=disclaimer-accepted`. A second Accept from a stale tab, after the first was recorded, lands on the error page
because the form is no longer on the page (Blazor refuses a post to a named form the page does not render); the
acceptance stands.

Tests: `dotnet test` runs offline (options, registration, hashing, email and password rules, throttle windows and the
parallel burst, session revalidation and revocation, cookie and antiforgery options, cookie events, return-URL guard,
the disclaimer's clauses, its summary and the hash of its text beside its version, the acceptance texts). Live checks run
against PostgreSQL through the same git-ignored `tests/.database.json` as the database module:
`dotnet test -- --filter-trait "Category=Live"` (sign-out ending every session, deletion removing the agents' log rows,
the acceptance recorded once per version, the acceptance posted from a live agent's page and its review step over HTTP
with the refused and the continued Start around it (`DisclaimerAcceptanceLiveTests`), and the configuration module's
pipeline tests signing up, in and out over HTTP).
