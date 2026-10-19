# Accounts module

Registration, sessions, password change, account deletion, fair-use limits and the risk disclaimer (SRS §3.2
FR-ACC-01..08; NFR-SEC-01/03; NFR-LEGAL-01/02; C-12, A-09). Other modules depend on `Ecnanib.Accounts.Abstractions`,
`AddAccounts(...)` and `MapAccountEndpoints()`; the hashing, cookie and throttle types never leave this folder. There
are users and nothing else: no email verification, roles, payments, plans, organizations or tenants (FR-ACC-05/06).
`AddAccounts` requires `AddDatabase` and `AddServer`.

| Need | Use |
|---|---|
| The signed-in user's id for any data access (NFR-SEC-03) | `HttpContext.User.RequireUserId()` (or `GetUserId()` when anonymous is possible); nothing else identifies a user |
| Register, authenticate, change password, delete (FR-ACC-01..04) | `IAccountService` → `RegistrationResult`, `AuthenticationResult`, `PasswordChangeResult`, `AccountDeletionResult`; user mistakes are results, exceptions mean the database failed |
| Start or end the cookie session after a result (FR-ACC-02) | `AccountSession.SignInAsync(httpContext, user)` from a static page handler; the layout's `POST /sign-out` form |
| Require a signed-in user on a page | `@attribute [Authorize]`; the middleware challenges with a redirect to `/sign-in?ReturnUrl=…` |
| Agent limits at creation and start (FR-ACC-07, FR-AGT-15) | `IFairUseLimits.CheckCreateAgentAsync` / `CheckStartAgentAsync` → `FairUseCheck` (allowed, current, max) |
| Disclaimer text, version and status (FR-ACC-08, FR-UI-05) | `RiskDisclaimer`, `<RiskDisclaimerNotice />`, `IAccountService.GetDisclaimerStatusAsync(...).IsAccepted`, which a live-mode start must require |

Rules the module enforces:

- Passwords are hashed with Argon2id (64 MiB, 3 iterations, 1 lane, 16-byte salt) in PHC form; a hash made with weaker
  parameters is upgraded at the user's next sign-in. Length is the only password rule: `Accounts:MinPasswordLength` to
  128 characters (NFR-SEC-01). Emails are trimmed and matched case-insensitively through `users.normalized_email`.
- One persistent, sliding cookie session (`ecnanib.session`: HttpOnly, SameSite=Lax, Secure when `Server:PublicUrl` is
  https, lifetime `Accounts:SessionDays`), protected by the Data Protection key ring the database module persists
  (FR-CFG-05) under the application name `Ecnanib`. Every sign-in mints a fresh ticket; nothing from before authentication is carried forward,
  and every state-changing POST, sign-out included, carries an antiforgery token (NFR-SEC-01).
- The ticket carries the user's security stamp. It is compared with the account every `Accounts:SessionRevalidationMinutes`,
  and at once when the user changed their password or deleted the account (an in-process revocation list; after a
  restart the interval alone bounds the gap). A password change ends every other session and re-issues the current one
  (FR-ACC-03); deletion ends them all.
- Sign-in is throttled in memory (one instance, C-10): `Accounts:SignInAttemptsPerWindow` failures per (email, client
  address) pair and `Accounts:SignInAttemptsPerAddress` failed sign-ins plus registrations per address, each within
  `Accounts:SignInWindowMinutes`. Keying the credential bucket by address too keeps an attacker from locking a user out of
  their own kill switch (FR-EXE-10). Wrong current passwords on the settings pages count as well. An unknown email costs
  the same as a wrong password and gets the same message. Behind a reverse proxy the address is the proxy's until
  forwarded headers are configured.
- Account deletion needs the password again (the explicit confirmation of FR-ACC-04) and every agent Stopped or Draft.
  One transaction deletes the allowed agents first, counts what remains, then removes the user's log entries (no foreign
  key, FR-LOG-08) and the user row, from which keys, agents, decisions, trades and acceptances cascade. An agent started
  concurrently either blocks the deletion or finds its row gone.
- Fair-use limits come from the `Agents` section and are identical for every user (FR-ACC-07); Running and Paused agents
  count as running, an agent in Error does not.
- The disclaimer is versioned (`RiskDisclaimer.Version`): one acceptance row per version, the first timestamp stands,
  `users.disclaimer_accepted_at` mirrors the latest. Changing the text means bumping the version, which asks everyone again.
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
(password, disclaimer status and acceptance, link to deletion), `/settings/delete-account`, and `POST /sign-out` from the
layout. The landing page shows the disclaimer (FR-UI-05). Theme and animation preferences belong to the user-interface issue.

Tests: `dotnet test` runs offline (options, registration, hashing, email and password rules, throttle windows, session
revalidation and revocation, cookie events, return-URL guard, disclaimer text). Live checks run against PostgreSQL through
the same git-ignored `tests/.database.json` as the database module: `dotnet test -- --filter-trait "Category=Live"`.
