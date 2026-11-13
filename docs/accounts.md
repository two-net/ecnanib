# Accounts

A visitor registers with an email address and a password, accepts the legal documents, and verifies the address with a link sent by email (FR-ACC-1, FR-ACC-7, FR-ACC-8, NFR-SEC-2). A user signs in with the address and the password, signs out, and resets a forgotten password by email (FR-ACC-2). Each of the three can be tried only so often (NFR-SEC-4). A browser that is signed in has a session, which ends after a period of inactivity (NFR-SEC-1). A user can turn on two-factor authentication with an authenticator app, and signing in then takes a code from the app after the password (FR-ACC-3). Sign-ins, re-authentications, new passwords, and the changes to two-factor authentication are recorded in the audit trail (NFR-LOG-7). Before an action that exposes keys or money, a user who is signed in proves again who they are (FR-ACC-4). A user can delete their account, which removes everything that belongs to it, and can download what is stored about them (FR-ACC-9, NFR-SEC-9). The only account type is the individual user (FR-ACC-5).

The code is in `src/Ecnanib/Accounts`, the pages in `src/Ecnanib/WebUi/Pages/Account` and `Pages/Legal`, and the email in `src/Ecnanib/Notifications`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Accounts` and `tests/Ecnanib.Tests/Notifications`.

How requests travel, over TLS and with which cookies, and how the secrets in the database are encrypted, is in [security.md](security.md). Whose data a request reaches, and who is an operator, is in [ownership.md](ownership.md).

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

The form can be sent only so often, from one client and for one address, so it sends no address more email than that. See "Limits on attempts".

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
| Of a later registration of the same address | Asks for a new password. With one, it verifies the address, replaces the password, and ends every session of the account. The new password is recorded in the audit trail. |

- The password typed on the form of a later registration is not kept. The page of the link asks for it, so the person who reads the mail of the address is the one who chooses it.
- `users.email_verification_sets_password` marks such a link. The migration that added the column marked every link that was out at the time, because nothing stored told a first link from a later one.
- **Not covered:** an owner who confirms the first link of a registration they did not make. The email tells them to ignore it. If they confirm it anyway, the account is verified with the password of whoever registered. A password reset is their way in, and it ends every session. It is no way in once whoever registered has turned on two-factor authentication, which a verified account can: a reset does not turn that off.

## Signing in

The form is at `/sign-in`. It asks for the email address and the password.

- The address is trimmed and compared in lower case, as it was stored.
- **A sign-in that fails gets one answer,** "The email address or the password is not right", whatever was wrong. An address without an account is checked against a hash that no password matches, so the time it takes is the same too.
- What no account can have is refused before anything is looked up or hashed: text that is not an email address, no password, or a password of more than 128 characters. The answer is the same one.
- **A password can be tried only so often,** from one client and for one address. After that the form answers "Too many attempts" for a while, whatever the password is, and whether or not the address has an account. See "Limits on attempts".
- A page that needs a signed-in user carries `@attribute [Authorize]`. A visitor who opens it is sent to `/sign-in?returnUrl=...` and comes back after signing in. `returnUrl` is followed only when it is a path of this application: it starts with one `/`, and holds no control character. Anything else leads to the start page. `/account` is the first such page.
- An account with two-factor authentication is not signed in by its password. It is asked for a code: see "Signing in with a code" below. Only the right password tells that an account has it on.
- The log names the account by its id: `User {UserId} signed in`, `A sign-in failed: ...`.
- A sign-in is recorded in the audit trail, in the transaction that starts the session. One that fails is not: see "The audit trail".

`ISignIn` does the work. The pages are rendered on the server for each request, so the password and the code travel in a form post.

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

Whatever needs the user of a request asks `SignedInUser.IdOf(request)`, and asks for that user's rows with `OwnedBy`. The handler also says whether the user is an operator, for the policy that an operator function requires. See [ownership.md](ownership.md).

Authentication runs ahead of the error handling, so the handler does not fail a request. When the database cannot be reached:

- The request goes on as not signed in, the browser keeps its cookie, and a warning is logged.
- A page that needs a signed-in user answers with the error page and its reference. It does not send a user who is signed in to the sign-in form.

### Signing out

The button posts to `/sign-out`, from any page. The session is deleted from the database and the cookie is taken from the browser, so a copy of the cookie stops working too. A sign-in of the browser that waits for a code ends in the same way.

A signed-in request must carry the token against request forgery. A request whose session has already ended is sent to the start page without one: the token of a page that is older than its session was made for the user the page showed.

## Resetting a password

The form is at `/forgot-password`, linked from the sign-in page. It asks for the email address.

| The address | What is stored | Email sent |
|---|---|---|
| Has an account | A new link, which ends an earlier one | The reset link |
| Has no account | Nothing | A notice that the address has no account |

As with registration, the visitor sees the same page, "Check your email", in both cases, and every request sends one email. If the email cannot be sent, the form says so.

A link can be asked for only so often, from one client and for one address. A request that is refused sends nothing and replaces no link, so the link that was sent last still works. See "Limits on attempts".

The email holds a link to `/reset-password?token=...` under `Application:PublicUrl`.

- The link works for one hour, once, and until a later link replaces it. A link that does not work gets one answer, whatever the reason.
- The token is stored as the verification token is: only its SHA-256 hash, under a parameter that the log masks.
- The new password meets the rules of registration. One that does not is refused, and the link still works.

Setting the password does five things:

1. The password hash is replaced and the link is spent.
2. **Every session of the account ends,** in the same transaction. Whoever was signed in knew the earlier password, or held a browser of someone who did. A sign-in that waits for a code ends with them.
3. A verification link that sets the password ends too, or whoever holds it could replace the password that was just set. A first link, which only verifies the address, stands.
4. The new password is recorded in the audit trail, in the same transaction, with where the request came from.
5. A notice is sent to the address, so that an owner who did not do this finds out. It is sent even when whoever set the password closes the connection at once. If it cannot be sent, the error is logged and the password is set all the same.

The user then signs in with the new password. A reset does not sign in, and **it does not confirm the address**: only a link from the registration form does, because that form is where the legal documents are accepted. **It does not turn two-factor authentication off** either, and it does not end a wait after too many wrong codes: whoever reads the mail of the address would otherwise need nothing else.

A sign-in with the earlier password cannot slip in behind a reset. Starting a session locks the account's row and checks that the password hash is still the one that was verified, and a reset changes that row before it ends the sessions. Whichever comes first, nobody stays signed in on the earlier password.

## Two-factor authentication

A user can protect their account with a second factor: a code from an authenticator app on their phone (FR-ACC-3). The app and the application share a key, and each works out the code of the present time from it. `ITwoFactor` turns it on and off, `ISignIn` asks for the code, and `TwoFactorCodes` judges a code wherever one is taken.

| What | Detail |
|---|---|
| The codes | Time-based one-time passwords as RFC 6238 defines them: HMAC-SHA1, six digits, a new code every 30 seconds. These are the values every authenticator app uses unless it is told otherwise. `Totp` makes them. .NET has what the algorithm needs, so no package is used. |
| The key | 20 random bytes. It is stored encrypted, and in no other form: see "The key" below. |
| Which codes are right | The code of the present 30 seconds, and of the 30 seconds before and after, for a clock that is a little off and for the time it takes to type. |
| Once | A code is accepted only for a later step of time than the last code that was accepted. So no code is accepted twice, also not when two requests bring it at the same time. A code that two of the three steps share is taken for the later one. |
| Recovery codes | Ten, for a user who has lost the app. See "Recovery codes" below. |

### Turning it on

The page is `/account/two-factor`, linked from `/account`.

1. The page shows a new key, as a QR code for the app to scan and as text for typing. The QR code carries what an app reads: the key, the name in `Application:Name`, and the address of the account, which the app shows beside the code.
2. The user enters the code that the app shows, and their password.
3. With both right, two-factor authentication is on. The answer shows the recovery codes.

| Rule | Why |
|---|---|
| **Nothing is stored until the first code is accepted.** Every opening of the page makes a new key. The form carries it back, encrypted for the account it was made for. | If a key were stored when the page is opened, someone who once held a session could open the page, note the key, and wait for the owner to confirm it. |
| The key of a page can be confirmed for 30 minutes (`TwoFactorRules.SetupLifetime`). Then the page offers another. | A page that was left open, by whoever was at the browser, is of no use to them later. |
| The password is asked for. | Someone at a browser that was left signed in could otherwise enroll an app of their own, and keep the account from its owner. |
| The address of the account must be verified. | An address that is not verified may have been registered by someone it does not belong to. Its owner can still take the account over by registering it again. They could not, if a code of the first registrant stood in the way. |
| Every other session of the account ends, and every sign-in that waits for a code. | They were started with the password alone. The browser that turned it on stays signed in. |
| The step of the first code is stored with the key. | The code that turned it on does not sign in as well. |

All of it is one transaction, which starts with one statement that sets the key only while the account has none, its address is verified, and its password is still the one that was checked. Of two requests at the same time, one turns it on and the other finds it on.

The QR code is drawn on the server, as part of the page, with the package `Net.Codecrete.QrCodeGenerator`, because .NET has no QR encoder of its own. The key goes to the browser of its user and nowhere else. The pages that show the key and the recovery codes are not stored by the browser: an answer with a form is sent with `Cache-Control: no-store`.

### Signing in with a code

The right password of an account with two-factor authentication starts **no session**. It starts a sign-in that waits for a code:

| What | Detail |
|---|---|
| In the database | A row of `pending_sign_ins`, with the hash of a token, as a session has. |
| In the browser | The cookie `__Host-ecnanib-sign-in`, which holds the token. It is not a session cookie, and opens no page. See [security.md](security.md). |
| How long | Ten minutes. Then the password is asked for again. A row that waited too long is deleted when the password of its account is next given. |
| The page | `/sign-in/code`, which the sign-in page leads to, with `returnUrl` carried along. It takes a code from the app or a recovery code, in one field. |

A code that is right ends the wait and starts the session, in one transaction. So **a session of an account with two-factor authentication never starts without a code that was accepted in the same transaction.** Four things keep it so:

- **The password step decides under its lock.** `Sessions.StartAsync` locks the row of the account, as it did before, and the same statement says whether the account asks for a code. Turning two-factor authentication on changes that row. So a password that arrives while it is being turned on waits, and then is asked for a code.
- **The code step locks the row first,** and then looks again whether the sign-in still waits. A password reset and turning two-factor authentication off both change the row and end the sign-ins that wait.
- **The code step ends the wait itself, or starts nothing.** Signing out ends a wait without the lock on the account. If it comes in while a code is judged, the code step finds the wait gone and is undone, and the code is not spent.
- **Whatever ends the sessions of an account ends its waiting sign-ins:** a password reset, the link of a repeat registration, and turning two-factor authentication on. So do turning it off and signing out, and giving a password again on the same browser.
- **Every transaction that changes the row takes its lock as the first thing it asks of the row.** None of them waits for another while holding a lock the other needs.
- **Two passwords of one account can be taken at the same time,** because the password step only shares the row. Each clears away the sign-ins of the account that waited too long, and leaves alone one that the other is ending, so neither waits for the other.

A browser that is signed in to one account and gives the password of another stays signed in to the first until the code is accepted.

### Wrong codes

A code of six digits can be found by trying, unless trying is limited.

| Rule | Detail |
|---|---|
| Five wrong codes in a row | Every code of the account is refused for 15 minutes. The fifth wrong code starts the wait, and the count starts again. The numbers are `TwoFactorRules.WrongCodesAllowed` and `TwoFactorRules.RefusedFor`. |
| While codes are refused | A code is not looked at, right or wrong, so trying gains nothing. A recovery code is not spent by it. |
| For the account | The count is the account's, whichever browser sends the codes and whichever form takes them: signing in, turning it off, making new recovery codes, or proving who one is again before a sensitive action. |
| A right code | Starts the count again. |
| What cannot be a code | Is refused and not counted: nothing, or text that is neither six digits nor sixteen letters and digits. |
| A right code from the app, a second time | Is refused and not counted. It was not a guess, and a form that is sent again by a reload is then harmless. |
| A recovery code, a second time | Is a wrong code, and is counted: its row is gone, so nothing tells it from a guess. |
| A wrong code | Is counted in the transaction that judged it, which is committed whatever the answer, and whether or not the browser is still there. |

At that rate, someone who has the password gets 480 tries a day at three codes in a million.

Only someone who has the password, or a browser that is signed in, can reach a form that takes a code, so only they can make the codes of an account be refused. The owner's answer to that is a new password, which also ends every session. The log has a warning for each wait: `The codes of user {UserId} are refused for {Minutes} minutes, after {WrongCodes} wrong ones in a row`.

### Recovery codes

Turning two-factor authentication on gives the user ten recovery codes, for the day they lose the app.

- A code is 80 random bits, shown as sixteen letters and digits in groups of four: `ABCD-EFGH-IJKL-MNOP`. It is the same in small letters, and with or without its hyphens.
- **A code works once.** It signs in, turns two-factor authentication off, makes new recovery codes, or is the code of a re-authentication, in place of a code from the app. Its row is deleted as it is found, so of two requests with one code, one finds it gone.
- **They are shown once,** in the answer that made them. The table `two_factor_recovery_codes` keeps only their SHA-256 hashes, so reading the database gives nobody a way in. A user who did not see that answer, because the form was sent twice or the connection broke, makes new ones.
- `/account/two-factor/recovery-codes` replaces all ten, for a user who has used some or lost the list. It takes a code. The earlier ones stop working, used or not.
- `/account` and `/account/two-factor` say how many are left.

### Turning it off

`/account/two-factor/turn-off` takes a code, from the app or a recovery code. The key and the recovery codes are deleted, and signing in takes the password alone again. This is also how a user moves to another phone: turn it off, and turn it on again with the new one.

### The key

The key is in `users.two_factor_secret`, encrypted by `StoredSecrets` with the active master key of the configuration file, and `users.two_factor_secret_key_id` names that key (NFR-SEC-5, FR-KEY-6). The value is bound to the account: copied to the row of another account, it does not decrypt. Accounts declares the column as a stored secret, so when the active master key has changed, the key is encrypted again with it at the next start, and the app of the user goes on working (FR-KEY-7). See "Stored secrets" in [security.md](security.md).

It is decrypted to judge a code, in memory, and forgotten when the code is judged (FR-KEY-9). No page shows it after it is turned on.

### Not covered

- A user who has lost both the app and the recovery codes cannot sign in. Nothing in the application turns two-factor authentication off for them. Until an operator can, it takes a statement on the database that sets the six `two_factor_` columns of the account to nothing (the count to 0) and deletes its recovery codes.
- No email tells the owner that two-factor authentication was turned on or off. The audit trail records it.

## Re-authentication

Being signed in is not enough for an action that exposes keys or money. Before one, the user proves again who they are (FR-ACC-4). `SensitiveAction` names these actions:

| Action | Member |
|---|---|
| Adding or replacing a key | `AddOrReplaceKey` |
| Switching an agent to Live | `SwitchAgentToLive` |
| Deleting the account | `DeleteAccount` |

| Rule | Detail |
|---|---|
| The proof | What signing in takes: the password, and for an account with two-factor authentication a code as well, from the app or a recovery code. |
| How long it holds | Ten minutes (`ReauthenticationRules.HoldsFor`), counted from the proof. An action that is done does not make it hold longer. |
| For which browser | The one that gave it. The proof is `sessions.reauthenticated_at`, in the row of that browser's session, and nowhere else. Another browser of the same user is asked itself. |
| Signing in | Is not a proof. A session starts without one, so the first sensitive action of a session always asks. |
| What ends it | Whatever ends the session: signing out, staying away for the period of inactivity, a password reset, and signing in again on the browser. |

### Doing something sensitive

Whatever does one of these actions asks `IReauthentication` first, on the server, in the request that asks for the action:

```csharp
if (!await reauthentication.IsRecentAsync(request, SensitiveAction.AddOrReplaceKey))
{
    // Not done.
}
```

- **The answer is read from the database at that moment,** for the session that the cookie of the request names. It is no for a request that is not signed in.
- **The action is not done when the answer is no.** A page then sends the browser to `SessionAuthentication.PageWith(SessionAuthentication.ReauthenticationPath, returnUrl)`, and the user comes back after the proof. A form that was posted is not posted again: the user fills it in again.
- **The check is in the action, and not only in the page that leads to it,** so it holds for a request that no page of the application made.
- The log says which action was held back: `User {UserId} is asked to confirm who they are before {Action}`.

Deleting the account is built: see "Deleting the account". It asks inside its own transaction, under the lock on the account's row, so that the answer holds until the account is gone. The other two actions are not built yet. Each calls this when it is.

### Proving it

The page is `/confirm-identity`, for signed-in users. It asks for the password, and for a code when the account has two-factor authentication on. `returnUrl` is followed as it is after signing in: only when it is a path of this application. Someone who has just proved it is sent on without being asked.

`IReauthentication.ConfirmAsync` takes the proof:

- **For an account with two-factor authentication the code is judged first, and a code that is not right is the answer whatever the password was.** So someone at a browser that was left signed in, who does not have the app, learns nothing about the password from the form, and their wrong codes are counted: see "Wrong codes".
- **A right code is spent even when the password is wrong,** a recovery code included. The page asks for the next one. If the code could be given again, one code would serve for as many passwords as can be tried in the minute and a half that it is right.
- The password is verified before the transaction, because a hash takes its time. The transaction then locks the row of the account, judges the code, looks whether the password is still the one that was verified, and writes the time into the session. A password reset ends the session, so a proof never outlives the password it was given with.
- If the session has ended by then, nothing is written, the code is not spent, and the browser is sent to sign in.
- The proof is recorded in the audit trail, in the transaction that writes it into the session. A recovery code that is used here is recorded as everywhere, ahead of the proof it gave.
- **The password of an account can be tried here only so often.** For an account without two-factor authentication, whoever holds a browser that is signed in could otherwise find the password by trying. A proof that is refused is refused before anything is looked at, so it spends no code. See "The password of an account that is signed in" under "Limits on attempts".
- The log names the account by its id: `User {UserId} confirmed who they are`, `User {UserId} did not confirm who they are: the password was not right`.

## Limits on attempts

Signing in, registering, and asking for a password reset link can each be tried only so often (NFR-SEC-4). Without a limit, a password could be guessed at without a pause, and the two forms that send email could send one address as much of it as someone likes. `AttemptLimits` counts the attempts, and each of the three asks it first.

| What | From one client | For one email address | Within | In the configuration file |
|---|---|---|---|---|
| Signing in with a password | 30 times | 10 times | 15 minutes | `Limits:SignIn` |
| Registering | 10 times | 3 times | 60 minutes | `Limits:Registration` |
| Asking for a reset link | 10 times | 3 times | 60 minutes | `Limits:PasswordReset` |

These are the defaults of the tracked configuration file. Each key holds `PerClient`, `PerAddress`, and `Minutes`, and applies to every user alike. See [configuration.md](configuration.md).

| Rule | Detail |
|---|---|
| Within a period | A client, or an address, has room for an attempt while it has fewer than its number in the minutes that end now. So it is refused from its last attempt until its oldest one is that many minutes old, and then has room for one. |
| A client | The IP address a request came from, as the connection knows it. An IPv6 address counts as its network of 64 bits, because one subscriber is given that many addresses. Requests whose connection names no address count as one client. |
| An address | The email address as it is stored: trimmed and in lower case. It is counted whether or not it has an account, so **being refused does not tell whether an address has an account**. |
| Both or neither | An attempt counts for its client and for its address. One that either of them refuses counts for neither: a client that is refused uses up nothing of the addresses it names. |
| Being refused | Is not counted. Trying again while refused does not make the wait longer. |
| What is refused for what it is | Is not counted either: text that is not an address, no password, a registration that breaks a rule. It costs nothing, and tells nothing. |
| Before anything else | The limit is asked before an account is looked up, a password is hashed, or an email is sent, and a refused attempt does none of that. A refused request for a reset link replaces no link. |
| The answer | The form again, with "Too many attempts. Try again in up to 15 minutes.", and status 429. It is the same whatever the password was. The minutes are those of the period, which is the longest the wait can be. |
| The log | `A sign-in was refused: too many attempts from one client`, or `for one address`. It names neither of them. |

At the default, the password of one account can be tried 960 times a day on the sign-in form, from however many clients.

### The password of an account that is signed in

Two more forms take a password, both from a browser that is signed in: the page that asks for it before a sensitive action, and the form that turns two-factor authentication on. Someone at a browser that was left signed in could try passwords on them.

- **The two forms together take as many tries as the sign-in form takes for one address:** `Limits:SignIn:PerAddress` in `Limits:SignIn:Minutes`. They are counted for the account, whichever of its browsers tries.
- **They are counted apart from the sign-in form.** Someone who tries the sign-in form for an address does not keep its owner, who is signed in, from a sensitive action.
- **A proof that is refused spends no code,** and counts none as wrong: it is refused before the code is looked at.
- On the form that turns two-factor authentication on, a code that is not right is told so first and is not a try of the password. Whoever is at the browser can make as many keys and codes as they like, so the code keeps nobody out there.
- The log names the account by its id: `User {UserId} was refused confirming who they are: too many attempts for one account`.

### Using up the attempts of someone else

**Someone can use up the attempts of an address that is not theirs.** Its owner is then refused on that form until an attempt is old enough. This is decided: a limit for each client alone does not hold back guessing that is spread over many clients, and a limit for the address does.

| Form | What its owner loses for the period | What they keep |
|---|---|---|
| Signing in | Signing in on a browser that is not signed in. | Every session that is open, and the sensitive actions on it. |
| Registering | Registering the address, or getting a new verification link. | The link that was sent last. |
| Asking for a reset link | Asking for another link. | The link that was sent last, which nobody can replace while the address is refused. |

### Where the counts are

- **In memory, in each running instance.** They start again when the application does, and a second instance would count on its own.
- **Nothing of them is stored.** The address of a client, and an email address that may have no account, are not kept about anyone (NFR-SEC-9).
- What no longer counts is dropped once in a period, so an address that was tried once does not stay in memory.
- **Behind a proxy, the application must be told to believe the proxy's headers,** or every visitor counts as the one client that the proxy is. See "Behind a proxy that terminates TLS" in [security.md](security.md).

### Not covered

- The pages of a link from an email, `/verify-email` and `/reset-password`, are not limited. Their token is 32 random bytes, which cannot be found by trying, and a link that does not work costs one query and no hash.
- The page that takes a code after the password has its own limit, for the account: see "Wrong codes".
- How many requests of any kind one client may make. That is for whatever stands in front of the application.

## Deleting the account

A user can delete their account (FR-ACC-9). The page is `/account/delete`, linked from `/account`. `IAccountDeletion` does the work.

| Rule | Detail |
|---|---|
| Re-authentication | Deleting the account is a sensitive action. The page is shown only to a user who has just proved again who they are: everyone else is sent to `/confirm-identity` and comes back. The button asks again, in the action. |
| Agents | The account is deleted only while every agent of the user is stopped. Otherwise the page says so, and nothing is deleted. See "Agents" below. |
| What goes | The row of `users`, and with it every row that belongs to the user, in every table. Stored keys are such rows, so they are gone at once. Nothing is marked now and removed later. |
| What stays | The rows of the service, which carry no owner. Statistics that count across users are such rows, once there are any. |
| Sessions | Every browser of the user is signed out. The browser that deleted the account loses its session cookie, and is sent to `/account/deleted`, a page for visitors. |
| The address | Has no account any more. Registering it again makes a new account, with nothing of the earlier one. |
| A last message | Tells the address that the account was deleted. If it cannot be sent, the error is logged and the account is deleted all the same. The address is kept nowhere after it. |
| The audit trail | Records nothing: the events of an account go with it. The log has `User {UserId} deleted their account, which ended {SessionCount} sessions`. |

### How it is done

Deleting an account is one transaction:

1. It locks the row of the account with `FOR UPDATE`, as the first thing it asks of the row.
2. Under that lock it asks whether the session of the request holds a proof that is recent enough.
3. It asks whether an agent of the user is not stopped.
4. It ends the sign-ins of the account that wait for a code, and then its sessions.
5. It deletes the row of `users`. The database deletes every row that points at it.

The cookie, the log event, and the last message come after the commit.

- **The lock is the lock of a deletion,** and not the `FOR NO KEY UPDATE` of the other changes to an account. A weaker lock would let a row that points at the account be added while it is being deleted. It is taken first, so the transaction never has to make a lock stronger while it holds one.
- **The proof is looked at under the lock.** A new password ends the session that holds the proof, and changes the row, so it comes before the deletion or waits for it. An account is never deleted on a proof that was given with a password which no longer is one.
- **The sessions are ended before the row is deleted,** in the order every other transaction ends them: the sign-ins that wait, then the sessions. The database would end them with the account, in an order of its own, and two transactions that lock the same rows in different orders can each end up waiting for the other.
- **A form that is sent twice is answered as the first one was.** A request that finds its account gone is told that it is deleted.
- **A request of another browser that is under way when the account goes is answered.** `ITwoFactor` says that an account which does not exist has nothing on and is not verified, and the export sends the browser to sign in. Its next request is a visitor's.
- A sign-in of the browser that waits for a code is left alone. It may be one of another account: see "Signing in with a code".

### Agents

Accounts depends on no other component, so it cannot look at agents. It declares `IAgentActivity`, and Agents implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IAgentActivity
{
    Task<bool> HasAgentsNotStoppedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken);
}
```

- **It is asked inside the transaction that deletes the account,** with the context that holds it.
- **An implementation locks the rows of the agents it reads** (`FOR UPDATE`). The lock on the account keeps an agent from being created, and does not keep one that exists from being started. With the rows locked, an agent that is started at the same moment is seen as not stopped, or waits and finds its account gone.
- **Nothing implements it yet,** because agents are not built. Until then no user has an agent, and nothing stands in the way. `AccountDeletionTests` has a test that fails as soon as Agents has a table of its own and no implementation.

### That nothing is left

| Rule | How it is enforced |
|---|---|
| The rows of a user go with the account. | Each foreign key that carries the owner to `users` deletes the row with what it points at, at every row on the way. `OwnershipModelTests` reads the model. See [ownership.md](ownership.md). |
| Deleting an account leaves no row of its user, and changes no row of another. | `AccountDeletionTests` gives two users a row in every table of the model that holds rows of users, deletes one account, and compares. A table that is added later fails that test until the test gives both users a row in it. |

## Exporting your data

A user can download what is stored about them (NFR-SEC-9). The link is on `/account`, and leads to `/account/export`. `IDataExport` reads the data, and an endpoint of the Web UI answers with it as a file.

| Section | What it holds |
|---|---|
| `exportedAt` | When the export was made. |
| `account` | The id, the email address, when it was registered and verified, and whether two-factor authentication is on, since when, and how many recovery codes are left. |
| `legalAcceptances` | Each document that was accepted, with its version and the time. |
| `sessions` | Each browser that is signed in: when it signed in, and when it was last used. |
| `auditEvents` | Each event of the account in the audit trail: the action, what it was done to, when, and the IP address it came from. |

| Rule | Detail |
|---|---|
| **It holds no secret** (FR-KEY-8). | Not the hash of the password, not the key of two-factor authentication, and not the hash of a token or of a recovery code. None of them says anything about the user, and each is of use only to someone who wants in. They are not read for an export at all. |
| It is the user's own. | The rows are asked for with `OwnedBy`, for the user of the session. See [ownership.md](ownership.md). |
| It is for a signed-in user. | A visitor is sent to sign in. It is not one of the three sensitive actions, so it asks for no proof. |
| It is a file. | JSON, one thing to a line, with the names of things and not their numbers. It is sent as an attachment called `account-data-<date>.json`, and with `Cache-Control: no-store`, so that neither the browser nor anything on the way keeps it. The link is marked `download`, so the browser asks for the file once. |
| Every table is decided. | `ExportRules` in [`DataExportTests.cs`](../tests/Ecnanib.Tests/Accounts/DataExportTests.cs) lists each table of users' rows as exported, or as not exported with why. A table that is added later fails the test until it is listed. |

Two tables are not exported. `pending_sign_ins` holds the hash of a token and a time, for ten minutes at most. `two_factor_recovery_codes` holds hashes: the export says how many codes are left.

The log has `User {UserId} exported their data`, and nothing of the data.

## The audit trail

Security- and money-relevant actions are recorded in the table `audit_events`, in the database and apart from the logs (NFR-LOG-7). Accounts records its own: sign-ins, re-authentications, new passwords, and the changes to two-factor authentication. The other components record theirs when they are built: see "What is not recorded yet".

| Column | What it holds |
|---|---|
| `user_id` | The user who did it. |
| `action` | What was done, as the name of a member of `AuditAction`. |
| `target` | What it was done to, as a kind and an id: `user:<id>` for an account. |
| `occurred_at` | When. |
| `origin` | The IP address the request came from, as the connection knows it, or nothing when it does not. |

| Action | Recorded when |
|---|---|
| `SignedIn` | A session started: with the password, and for an account with two-factor authentication when the code was accepted. |
| `Reauthenticated` | The user proved again who they are, before a sensitive action. |
| `PasswordChanged` | A new password was set with a reset link, or with the link of an address that was registered again. |
| `TwoFactorTurnedOn` | Two-factor authentication was turned on. |
| `TwoFactorTurnedOff` | It was turned off. |
| `TwoFactorRecoveryCodesReplaced` | New recovery codes were made. |
| `TwoFactorRecoveryCodeUsed` | A recovery code was used, for any of the four things a code does. |

- **An event is written in the transaction of the action it records,** by `AuditTrail.RecordAsync`. It is stored when the action is, and not when it is not. `RecordAsync` refuses a context that holds no transaction.
- **A sign-in is recorded where the session is stored,** in `Sessions.AddAsync`, which is the one place. So no session starts without its event. A password that only leads to the page that asks for a code records nothing: nobody is signed in.
- **An event holds no secret, and cannot.** Nothing that makes one takes text: the action is a member of `AuditAction`, the target is made from an id by `AuditTarget`, and the origin is read from the connection by `AuditOrigin`. `AuditEvent` has no constructor that anything else can call. So what a request brought, such as a key or a password, cannot be written into the trail, whatever records the event.
- **The ids of events are in the order the events were recorded in.** An id starts with a time in milliseconds, and `AuditTrail` hands each one out for a later millisecond than the one before. So two events of one transaction are in order too: a recovery code, and then the sign-in it made. This holds for the events that one running instance records.
- **The origin is personal data,** and it is not on the list of NFR-SEC-9. It is kept because an audit trail that cannot say where an action came from does not show a takeover. It is the one thing stored about a person beyond that list: see "Personal data". It is in the export of the user's data.
- **The events of an account are deleted with the account.** So deleting an account is not an event: the log says that it was deleted.
- Behind a proxy, the origin is the address of the browser only when the application is told to believe the proxy's headers. Otherwise it is the address of the proxy. See "Behind a proxy that terminates TLS" in [security.md](security.md).

Two actions are recorded though the requirement does not name them. A re-authentication says who was at the browser when keys or money were at stake. A new password by a link from the mail is how an account is taken over by whoever reads that mail.

### What is not recorded

| What | Why |
|---|---|
| A sign-in that fails, and a code that is not right | An event belongs to an account, and an address without an account has none to belong to. Recording only for the addresses that have one would make a failed sign-in do other work for them, and so tell whether an address has an account. They are in the log, and wrong codes are counted on the account. |
| Signing out | A session also ends by staying away, which is nobody's action. |
| Registering, and verifying an address with a first link | They sign nobody in, and change no password. |
| Deleting the account | The events of an account go with it. |

### Recording an event

Whatever does one of the actions of the requirement records it, in its own transaction. Agents are not built yet. Starting one will record it like this:

```csharp
await AuditTrail.RecordAsync(
    context, userId, AuditAction.AgentStarted, AuditTarget.Agent(agent.Id), now, AuditOrigin.Of(request), cancellationToken);
```

1. Add the action to `AuditAction`, and a method for the kind of target to `AuditTarget` when the kind is new. Both are in Accounts, so that the vocabulary of the trail is in one place.
2. Record inside the transaction that does the action, with the context that holds it. `RecordAsync` saves that context.
3. Take the origin from the request, with `AuditOrigin.Of`.
4. Write a test that does the action and finds the event, and mark it `[ShowsAuditOf(AuditAction.…)]`.
5. List the action in [`AuditRules.cs`](../tests/Ecnanib.Tests/Accounts/AuditRules.cs), under the action of the requirement that it records.

A component that may not use Accounts, such as the Decision Engine, records nothing itself. Whoever takes the request does: it opens the transaction, records the event, and hands its context down.

### What is not recorded yet

`AuditRules.cs` is the source of truth. It lists the actions that the requirement names, each with the members of `AuditAction` that record it, or with what it waits for:

| Action of the requirement | Waits for |
|---|---|
| Key changes | Credentials |
| Agent start, pause, and stop | Agents |
| Mode switches | Agents |
| Manual closes | Agents, which takes the request. The Decision Engine places the order. |
| Risk setting changes | Agents |
| Operator actions | The first operator function |

`AuditRulesTests` fails for an action that still waits once what it waits for is built: once the component has a table of its own, or, for the operator actions, once a page or an endpoint requires the operator policy or something outside Accounts calls `OperatorAccess.IsOperator`. It also fails for a member of `AuditAction` that no marked test shows being recorded.

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
| `Limits:SignIn`, `Limits:Registration`, `Limits:PasswordReset` | How often each may be tried. See "Limits on attempts". |

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

## Personal data

Personal data is limited to the email address, the password hash, and preferences (NFR-SEC-9).

| What | Where it is stored |
|---|---|
| The email address | `users.email` |
| The password hash | `users.password_hash` |
| Preferences | None is stored yet. |
| **Beyond the list:** the IP address a security-relevant action came from | `audit_events.origin`. See "The audit trail". |

- **The origin is the one exception,** and it is a decision: the specification lists it for an audit event, and an audit trail without it does not show a takeover. It is in the export, and goes with the account.
- [`PersonalDataRules.cs`](../tests/Ecnanib.Tests/Persistence/PersonalDataRules.cs) is the source of truth. It lists every column of `users` and of the tables of users' rows with what it holds, unless the type of the column cannot say who someone is: an id, a flag, a number, a time, or one of a fixed set of names.
- `PersonalDataTests` reads the model. A column that is added and not listed fails it, so what is kept about a person is decided in one visible place. The two lists of personal data are pinned there.
- The rule reads types and not meanings. A time, such as when a session was last used, is listed nowhere. It is in the export, and goes with the account.
- The id of an account tells nothing about the person, so it is not personal data. See [ownership.md](ownership.md).
- The logs hold neither an address nor an origin: they name an account by its id. See [logging.md](logging.md).

A user can export this data and delete it: see "Exporting your data" and "Deleting the account".

## The data

| Table | Columns |
|---|---|
| `users` | `id`, `email` (unique), `password_hash`, `registered_at`, `email_verified_at`, `email_verification_token_hash` (unique), `email_verification_expires_at`, `email_verification_sets_password`, `password_reset_token_hash` (unique), `password_reset_expires_at`, `two_factor_secret`, `two_factor_secret_key_id`, `two_factor_enabled_at`, `two_factor_last_step`, `two_factor_failed_codes`, `two_factor_locked_until` |
| `legal_acceptances` | `user_id`, `document`, `version`, `accepted_at`. The rows of an account are deleted with it. |
| `sessions` | `id`, `user_id`, `token_hash` (unique), `created_at`, `last_active_at`, `reauthenticated_at`. The rows of an account are deleted with it. |
| `pending_sign_ins` | `id`, `user_id`, `token_hash` (unique), `expires_at`. The rows of an account are deleted with it. |
| `two_factor_recovery_codes` | `user_id`, `code_hash`, `created_at`. The rows of an account are deleted with it. |
| `audit_events` | `id`, `user_id`, `action`, `target`, `occurred_at`, `origin`. The rows of an account are deleted with it. |

An account's id is a version 7 UUID that the application assigns. A table that belongs to a user points at `users.id` (NFR-DAT-9). The five tables below `users` are such tables: their entities implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. A session and a sign-in that waits are also found by the hash of their token, which is how a request comes to be signed in. See [ownership.md](ownership.md).

Deleting an account deletes its row of `users`, and the database deletes the rows of the five tables with it. See "Deleting the account".

`users` has no column for a kind of account, because there is one kind. Who is an operator is in the configuration file and is stored nowhere.

The first three `two_factor_` columns are empty together or set together: two-factor authentication is on exactly while `two_factor_enabled_at` is set. None of the six has an index.

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
- Limits on attempts that outlast a restart, or that several instances share. The counts are in memory: see "Limits on attempts".
- The other two sensitive actions: adding or replacing a key, and switching an agent to Live. Each asks `IReauthentication` first when it is built. See "Re-authentication".
- What Agents says about the agents of a user before an account is deleted. It implements `IAgentActivity` when agents are built. See "Deleting the account".
- The parts of the export that other components keep: keys in their masked form, agents, lessons. Accounts cannot read their rows, so each hands its part over through an interface that Accounts declares, when the first such table arrives.
- Preferences. None is stored yet. When one is, it is personal data of an allowed kind, and it is in the export.
- Keeping the audit events of operator actions when the operator deletes their own account. No operator action is built yet. When one is, either an operator cannot delete their account, or those events are kept apart from it. An action on the account of another user names that account as its target, under the operator's own: whose export and whose deletion such an event follows is decided then as well.
- The rest of the audit trail: the actions of Credentials, of Agents, and of operators, each of which records its own when it is built, and the job that removes events older than `Retention:AuditDays`. See "What is not recorded yet".
- A screen that shows a user, or an operator, the audit trail. A user reads theirs in the export.
- A way for an operator to turn two-factor authentication off for a user who has lost both the app and the recovery codes, and an email that tells the owner when it is turned on or off.
- What a protected endpoint under `/api` answers to a request that is not signed in. Today it would be sent to the sign-in page, as a page is. No such endpoint exists yet.
- Ending a session inside an interactive page. A page that is rendered for each request is checked on each request. An interactive page keeps a connection open, which is not checked again after it is made. No page is interactive yet; the first one must check the session itself.
- Removing accounts that were never verified, and the sessions and waiting sign-ins of users who never come back.
- The wording of the legal documents.
