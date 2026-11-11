# Accounts

A visitor registers with an email address and a password, accepts the legal documents, and verifies the address with a link sent by email (FR-ACC-1, FR-ACC-7, FR-ACC-8, NFR-SEC-2). The only account type is the individual user.

The code is in `src/Ecnanib/Accounts`, the pages in `src/Ecnanib/WebUi/Pages/Account` and `Pages/Legal`, and the email in `src/Ecnanib/Notifications`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Accounts` and `tests/Ecnanib.Tests/Notifications`.

Sign-in, sign-out, password reset, and two-factor authentication are separate features and are not built yet.

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
| Has an account that is not verified | Only a new link, which ends the earlier one | A verification link, which says that the first password still applies |
| Has a verified account | Nothing | A notice that someone tried to register, and that nothing changed |

Two things follow from this on purpose:

- **The form does not tell whether an address has an account.** The answer is the same, and so is the work done: every registration hashes the password, writes to the database, and sends one email.
- **A registration never changes an account that exists.** If it replaced the password of an account that is not verified yet, anyone who knows the address could take the account over.

If the email cannot be sent, the form says so and the error is logged. The account stays, and registering again sends a new link. That is also how to get a new link after one has expired, until sign-in brings a button for it.

The log names an account by its id and never by its address.

## Verifying the address

The email holds a link to `/verify-email?token=...` under `Application:PublicUrl`.

- The link works for 24 hours, once, and until a later link replaces it. A link that does not work gets one answer, whatever the reason.
- Opening the link shows a page with a button. The address is verified when the button is pressed, so a mail scanner that opens every link in a message verifies nothing.
- The token is 32 random bytes. The database keeps only its SHA-256 hash, so reading the database gives nobody a link that works.
- The query parameter is called `token`, which the masking of the log recognises, so a logged request shows `token=***`. See [logging.md](logging.md).

A user whose address is not verified cannot create agents. See "Creating agents" below.

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
| `users` | `id`, `email` (unique), `password_hash`, `registered_at`, `email_verified_at`, `email_verification_token_hash` (unique), `email_verification_expires_at` |
| `legal_acceptances` | `user_id`, `document`, `version`, `accepted_at`. The rows of an account are deleted with it. |

An account's id is a version 7 UUID that the application assigns. A table that belongs to a user points at `users.id` (NFR-DAT-9).

## Email

Accounts depends on no other component, so it cannot call Notifications. It declares `IAccountMailer`, and Notifications implements it: `AccountMailer` writes the two messages and hands them to `IEmailAdapter`. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

The adapter is `SmtpEmailAdapter` in `Notifications/Adapters/Smtp`. It sends through the mail server of the `Email` section, with the package MailKit, over a connection of its own for each message. It logs each send with its outcome and duration, and never the address.

The file has no key for encryption. The port and the sign-in decide it:

| `Email` section | Connection |
|---|---|
| `Port` is 465 | Encrypted from the start. |
| `User` and `Password` are set | The server must offer STARTTLS, and the sign-in is sent only after it. A server that does not offer it gets nothing. |
| `User` and `Password` are empty | STARTTLS when the server offers it, and none when it does not. This is for a relay on the same machine. |

For development, `docker compose up -d` starts a mail server on `localhost:1025`, which the tracked configuration file names. It delivers nothing: read what the application sent at `http://localhost:8025`.

## Not built yet

- Sign-in, sign-out, password reset, sessions, and a button that sends the verification link again.
- Limits on how often one visitor may register. Until then, the form can be used to send email to an address repeatedly.
- Removing accounts that were never verified.
- The wording of the legal documents.
