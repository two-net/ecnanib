# Accounts

A visitor registers with an email address and a password, accepts the legal documents, and verifies the address with a link sent by email (FR-ACC-1, FR-ACC-7, FR-ACC-8, NFR-SEC-2). A user signs in with the address and the password, signs out, and resets a forgotten password by email (FR-ACC-2). A browser that is signed in has a session, which ends after a period of inactivity (NFR-SEC-1). The only account type is the individual user.

The code is in `src/Ecnanib/Accounts`, the pages in `src/Ecnanib/WebUi/Pages/Account` and `Pages/Legal`, and the email in `src/Ecnanib/Notifications`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Accounts` and `tests/Ecnanib.Tests/Notifications`.

Two-factor authentication is a separate feature and is not built yet. How requests travel, over TLS and with which cookies, is in [security.md](security.md).

## Registering

The form is at `/register`. It asks for an email address, a password, and a tick for each of the three legal documents.

| Rule | Detail |
|---|---|
| Email address | One `@`, with a name before it and a domain with a dot after it. It is trimmed and stored in lower case, so one address is one account however it is typed. |
| Password | 12 to 128 characters, counted as a person counts them. There are no rules about which characters. |
| Legal documents | Each one accepted, in the version that is in force. |

What happens next depends on the address, but the visitor sees the same page, "Check your email", in every case:

| The address | What is stored | Email sent |
|---|---|---|
| Has no account | The account, not verified, and the three acceptances | A verification link |
| Has an account that is not verified | Only a new link, which ends the earlier one and sets the password when it is confirmed | A verification link, which says that confirming it means choosing a new password |
| Has a verified account | Nothing | A notice that someone tried to register, and that nothing changed |

Two things follow from this on purpose:

- **The form does not tell whether an address has an account.** The answer is the same, and so is the work done: every registration hashes the password, writes to the database, and sends one email.
- **A registration never changes an account that exists.** If it replaced the password of an account that is not verified yet, anyone who knows the address could take the account over.
- **The password of an address that was registered twice is chosen by whoever confirms it.** See "An address that was registered again" below.

If the email cannot be sent, the form says so and the error is logged. The account stays, and registering again sends a new link. That is also how to get a new link after one has expired. Such a link is one of an address that was registered again, so it asks for a password.

The log names an account by its id and never by its address.

## Verifying the address

The email holds a link to `/verify-email?token=...` under `Application:PublicUrl`.

- The link works for 24 hours, once, and until a later link replaces it. A link that does not work gets one answer, whatever the reason.
- Opening the link shows a page with a button. The address is verified when the button is pressed, so a mail scanner that opens every link in a message verifies nothing.
- The token is 32 random bytes. The database keeps only its SHA-256 hash, so reading the database gives nobody a link that works.
- The query parameter is called `token`, which the masking of the log recognises, so a logged request shows `token=***`. See [logging.md](logging.md).

A user whose address is not verified can sign in, and cannot create agents. See "Creating agents" below.

### An address that was registered again

Someone can register an address that is not theirs, with a password of their own, and sign in to the account. If the first password still applied when the owner of the address registers later and confirms it, the owner would have confirmed an account that someone else can sign in to.

So the link of a repeat registration sets the password:

| Link | Confirming it |
|---|---|
| Of the first registration | Verifies the address. It takes no password. |
| Of a later registration of the same address | Asks for a new password. With one, it verifies the address, replaces the password, and ends every session of the account. |

- The password typed on the form of a later registration is not kept. The page of the link asks for it, so the person who reads the mail of the address is the one who chooses it.
- `users.email_verification_sets_password` marks such a link. The migration that added the column marked every link that was out at the time, because nothing stored told a first link from a later one.
- **Not covered:** an owner who confirms the first link of a registration they did not make. The email tells them to ignore it. If they confirm it anyway, the account is verified with the password of whoever registered. A password reset is their way in, and it ends every session.

## Signing in

The form is at `/sign-in`. It asks for the email address and the password.

- The address is trimmed and compared in lower case, as it was stored.
- **A sign-in that fails gets one answer,** "The email address or the password is not right", whatever was wrong. An address without an account is checked against a hash that no password matches, so the time it takes is the same too.
- What no account can have is refused before anything is looked up or hashed: text that is not an email address, no password, or a password of more than 128 characters. The answer is the same one.
- A page that needs a signed-in user carries `@attribute [Authorize]`. A visitor who opens it is sent to `/sign-in?returnUrl=...` and comes back after signing in. `returnUrl` is followed only when it is a path of this application: it starts with one `/`, and holds no control character. Anything else leads to the start page. `/account` is the first such page.
- The log names the account by its id: `User {UserId} signed in`, `A sign-in failed: ...`.

`ISignIn` does the work. The pages are rendered on the server for each request, so the password travels in a form post.

## Sessions

Signing in starts a session: a row of the `sessions` table, and a cookie in the browser that names it.

| What | Detail |
|---|---|
| The token | 32 random bytes, in the cookie. The table keeps only its SHA-256 hash, so reading the database gives nobody a session. |
| The cookie | `__Host-ecnanib-session`: `Secure`, `HttpOnly`, `SameSite=Lax`, for the whole host, with no expiry date. It ends with the browser. The `__Host-` prefix makes a browser refuse it unless this host set it over TLS. |
| One for each browser | Signing in again on a browser ends the session it had. A user can be signed in on several browsers. |
| Inactivity | A session ends when it has not been used for `Security:SessionIdleMinutes` (NFR-SEC-1). The setting is read at startup and applies to the sessions that exist. |

Each signed-in request counts as activity. It is not written on every request: a session whose stored time is less than a minute old is left as it is (less than that when the period is under an hour). The period counts from the stored time, which is never later than the last request, so **a session never lasts longer than configured**, and can end up to a minute early.

A session that has ended is deleted when its cookie is next seen, and the cookie is taken from the browser. So is a cookie that names no session, or that does not hold a token at all. The sessions of a user that ended on browsers which never came back are deleted when the user signs in. Those of a user who never signs in again stay until the account is deleted; a job that removes them belongs with the retention jobs.

### How a request is signed in

`SessionAuthenticationHandler` is the application's one authentication scheme, registered by `AddAccounts()`. It reads the cookie, finds the session and its account with one query, and makes the account the user of the request: the name identifier is the account's id, and the name is its address. The Web UI and the API see the same user, and every log event of the request carries the id as `UserId`. See [logging.md](logging.md).

Authentication runs ahead of the error handling, so the handler does not fail a request. When the database cannot be reached:

- The request goes on as not signed in, the browser keeps its cookie, and a warning is logged.
- A page that needs a signed-in user answers with the error page and its reference. It does not send a user who is signed in to the sign-in form.

### Signing out

The button posts to `/sign-out`, from any page. The session is deleted from the database and the cookie is taken from the browser, so a copy of the cookie stops working too.

A signed-in request must carry the token against request forgery. A request whose session has already ended is sent to the start page without one: the token of a page that is older than its session was made for the user the page showed.

## Resetting a password

The form is at `/forgot-password`, linked from the sign-in page. It asks for the email address.

| The address | What is stored | Email sent |
|---|---|---|
| Has an account | A new link, which ends an earlier one | The reset link |
| Has no account | Nothing | A notice that the address has no account |

As with registration, the visitor sees the same page, "Check your email", in both cases, and every request sends one email. If the email cannot be sent, the form says so.

The email holds a link to `/reset-password?token=...` under `Application:PublicUrl`.

- The link works for one hour, once, and until a later link replaces it. A link that does not work gets one answer, whatever the reason.
- The token is stored as the verification token is: only its SHA-256 hash, under a parameter that the log masks.
- The new password meets the rules of registration. One that does not is refused, and the link still works.

Setting the password does four things:

1. The password hash is replaced and the link is spent.
2. **Every session of the account ends,** in the same transaction. Whoever was signed in knew the earlier password, or held a browser of someone who did.
3. A verification link that sets the password ends too, or whoever holds it could replace the password that was just set. A first link, which only verifies the address, stands.
4. A notice is sent to the address, so that an owner who did not do this finds out. It is sent even when whoever set the password closes the connection at once. If it cannot be sent, the error is logged and the password is set all the same.

The user then signs in with the new password. A reset does not sign in, and **it does not confirm the address**: only a link from the registration form does, because that form is where the legal documents are accepted.

A sign-in with the earlier password cannot slip in behind a reset. Starting a session locks the account's row and checks that the password hash is still the one that was verified, and a reset changes that row before it ends the sessions. Whichever comes first, nobody stays signed in on the earlier password.

## Passwords

A password is stored only as a salted hash from Argon2id, a memory-hard algorithm (NFR-SEC-2). `PasswordHasher` makes and verifies the hashes, with the package `Konscious.Security.Cryptography.Argon2`, because .NET has no memory-hard algorithm of its own.

```
$argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>
```

| Part | Value |
|---|---|
| Memory | 19 MiB (`m=19456`, in KiB) |
| Passes | 2 |
| Lanes | 1 |
| Salt | 16 random bytes, new for every hash |
| Hash | 32 bytes |

These are the smallest parameters that OWASP's password storage guidance recommends for Argon2id. A hash names its own parameters, so they can be raised in `PasswordHasher` at any time: hashes made earlier still verify.

A password is brought to one Unicode form (NFKC) before it is hashed, so the same password typed on another device gives the same hash. No more hashes run at once than the machine has processors, because each holds its memory for as long as it runs.

The password is passed to `IRegistration` on its own, never inside a value that can be printed, and the form does not write it back into the page.

## Legal documents

At registration the user accepts the Terms of Service, the Privacy Policy, and the risk disclosure, and the accepted versions and the time are recorded (FR-ACC-8).

| What | Where |
|---|---|
| The texts | Pages of the Web UI: `/legal/terms`, `/legal/privacy`, `/legal/risk-disclosure`, in `WebUi/Pages/Legal`. They are drafts at version `0.1` until the legal wording is written. |
| The version in force | `LegalDocuments.CurrentVersionOf` in `Accounts/LegalDocuments.cs`. |
| What a user accepted | The table `legal_acceptances`: one row for each user, document, and version, with the time. |

Each tick box on the form carries the version the page showed. A registration is refused unless it carries the version in force for every document, so nobody is recorded as accepting a text they did not see.

**To change a text,** change its page and its version in `LegalDocuments` in the same commit. A row of `legal_acceptances` is never changed: accepting a later version adds a row.

## The registration switch and fair-use limits

Both come from the configuration file, are read at startup, and apply to every user alike (FR-ACC-7). See [configuration.md](configuration.md).

| Key | Effect |
|---|---|
| `Application:RegistrationOpen` | When `false`, the form is replaced by a notice and no account is created. An address that registered earlier can still be verified. |
| `Limits:MaxAgentsPerUser` | The most agents one user may have. No account carries a limit of its own. |

### Creating agents

Whatever creates an agent asks `IFairUse` first:

```csharp
var answer = await fairUse.MayCreateAgentAsync(userId, agentsOwned, cancellationToken);
```

| Answer | Meaning |
|---|---|
| `Allowed` | The user may create one more agent. |
| `EmailNotVerified` | The user has not verified their address (FR-ACC-1). |
| `AgentLimitReached` | The user has `Limits:MaxAgentsPerUser` agents already. |

## The data

| Table | Columns |
|---|---|
| `users` | `id`, `email` (unique), `password_hash`, `registered_at`, `email_verified_at`, `email_verification_token_hash` (unique), `email_verification_expires_at`, `email_verification_sets_password`, `password_reset_token_hash` (unique), `password_reset_expires_at` |
| `legal_acceptances` | `user_id`, `document`, `version`, `accepted_at`. The rows of an account are deleted with it. |
| `sessions` | `id`, `user_id`, `token_hash` (unique), `created_at`, `last_active_at`. The rows of an account are deleted with it. |

An account's id is a version 7 UUID that the application assigns. A table that belongs to a user points at `users.id` (NFR-DAT-9).

## Email

Accounts depends on no other component, so it cannot call Notifications. It declares `IAccountMailer`, and Notifications implements it: `AccountMailer` writes the messages and hands them to `IEmailAdapter`. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

The adapter is `SmtpEmailAdapter` in `Notifications/Adapters/Smtp`. It sends through the mail server of the `Email` section, with the package MailKit, over a connection of its own for each message. It logs each send with its outcome and duration, and never the address.

The file has no key for encryption. The port and the sign-in decide it:

| `Email` section | Connection |
|---|---|
| `Port` is 465 | Encrypted from the start. |
| `User` and `Password` are set | The server must offer STARTTLS, and the sign-in is sent only after it. A server that does not offer it gets nothing. |
| `User` and `Password` are empty | STARTTLS when the server offers it, and none when it does not. This is for a relay on the same machine. |

For development, `docker compose up -d` starts a mail server on `localhost:1025`, which the tracked configuration file names. It delivers nothing: read what the application sent at `http://localhost:8025`.

## Not built yet

- A button that sends the verification link again. Until then, a user gets a new link by registering again, which is not possible while registration is closed.
- Limits on how often one visitor may register, sign in, or ask for a reset link. Until then, the registration form and the reset form can be used to send email to an address repeatedly, and a password can be guessed at without a pause.
- Two-factor authentication, re-authentication before sensitive actions, and the audit trail, which will record sign-ins.
- What a protected endpoint under `/api` answers to a request that is not signed in. Today it would be sent to the sign-in page, as a page is. No such endpoint exists yet.
- Ending a session inside an interactive page. A page that is rendered for each request is checked on each request. An interactive page keeps a connection open, which is not checked again after it is made. No page is interactive yet; the first one must check the session itself.
- Removing accounts that were never verified, and the sessions of users who never come back.
- The wording of the legal documents.
