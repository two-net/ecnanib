# Security

How requests travel between a browser and the application: over TLS, with cookies that are sent only over TLS and that scripts cannot read (NFR-SEC-1). How the application defends against injection, cross-site scripting, request forgery, and clickjacking (NFR-SEC-4). And how a secret is kept in the database: encrypted (NFR-SEC-5).

The options, the headers, and the encryption are in `src/Ecnanib/Platform/Security`, and the order of the request pipeline is in `src/Ecnanib/Program.cs`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Security`, `tests/Ecnanib.Tests/Accounts`, and `tests/Ecnanib.Tests/Architecture`.

Sign-in, sessions, password reset, two-factor authentication, and re-authentication before a sensitive action are in [accounts.md](accounts.md). So is what is stored about a person, and how a user exports and deletes it (NFR-SEC-9). Whose data a request reaches, and the check that an operator function requires, are in [ownership.md](ownership.md). How often signing in, registering, and asking for a password reset may be tried is under "Limits on attempts" in [accounts.md](accounts.md).

## TLS

"All traffic" here means the traffic between browsers or API clients and the application.

| Rule | How |
|---|---|
| A request over plain HTTP is not served. | It is answered with status 307 and the same address over TLS, at the port of `Application:PublicUrl`. This holds for pages, the API, and static files, and for a form that is posted. |
| The address users reach the application at is an `https` address. | `Application:PublicUrl` is checked at startup, with the rest of the configuration file. The links in emails are built from it. See [configuration.md](configuration.md). |
| A browser that has been here does not try plain HTTP again. | Outside the Development environment every answer over TLS carries `Strict-Transport-Security: max-age=31536000`, which is one year. Subdomains are not included: they are not this application's to decide for. An answer to `localhost` never carries it. |

The connections the application opens itself follow their own rules. The connection to the mail server is described under "Email" in [accounts.md](accounts.md). The connection to the database is what `ConnectionStrings:EcnanibDbContext` says.

### For development

The tracked configuration file names `https://localhost:7109`, and the launch profile listens there and on `http://localhost:5195`, which redirects. Trust the development certificate once:

```
dotnet dev-certs https --trust
```

### Behind a proxy that terminates TLS

When a proxy takes the TLS connections and passes the requests on over plain HTTP, the application must be told which requests arrived over TLS, or it redirects every one of them. Set the platform's own variable:

```
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

The application then believes the `X-Forwarded-Proto` and `X-Forwarded-For` headers of every request, whoever sent it. **Only the proxy may be able to reach the application's port.** Anyone else who can reach it can claim that a plain request arrived over TLS, and can claim to come from any address.

The variable also decides where a request is taken to come from. The limits on attempts count by that address, and the audit trail records it. **Without the variable, every request comes from the proxy:** all visitors then count as one client, and reach the limit of one client together. See "Limits on attempts" in [accounts.md](accounts.md).

## Cookies

Every cookie the application sets is `Secure` and `HttpOnly`, and at least `SameSite=Lax`. One place enforces it, the cookie policy at the start of the pipeline, so it holds for a cookie that a later feature or the framework adds.

| Cookie | Purpose | Attributes |
|---|---|---|
| `__Host-ecnanib-session` | The token of a session. See "Sessions" in [accounts.md](accounts.md). | `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, no expiry |
| `__Host-ecnanib-sign-in` | The token of a sign-in that waits for a code, from the page that took the password to the page that takes the code. It is set only for an account with two-factor authentication, and taken back when the code is accepted. See "Signing in with a code" in [accounts.md](accounts.md). | `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no expiry |
| `__Host-ecnanib-antiforgery` | One half of the token against request forgery that every form carries. | `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no expiry |

The `__Host-` prefix makes a browser refuse a cookie unless it was set over TLS, for the whole host, and without a domain. So a site on another subdomain of the same domain cannot plant any of these cookies.

Because the cookies are `Secure`, a browser does not send them over plain HTTP. The tests speak TLS to the application for that reason.

## Defenses of a web application

The application defends against injection, cross-site scripting, request forgery, and clickjacking (NFR-SEC-4). Each defense below names the test or the rule that holds it in place.

### What every answer tells the browser

`BrowserDefenses` in `Platform/Security` sets four headers on every answer: a page, an answer of the API, a static file, the error page, and the redirect of plain HTTP. It is the first step of the pipeline, and it sets them as the answer starts, so that handling an error does not lose them.

| Header | Value | What a browser does with it |
|---|---|---|
| `Content-Security-Policy` | The policy below. | Runs no script and applies no style that is not the application's own, and shows the application in no frame. |
| `X-Frame-Options` | `DENY` | The same about frames, for a browser that does not know `frame-ancestors`. |
| `X-Content-Type-Options` | `nosniff` | Takes a file for what its content type says it is, and never guesses it to be a script. |
| `Referrer-Policy` | `same-origin` | Tells no other site the address of a page. The address of a link from an email holds its token. |

The policy, with the nonce of the answer:

```
default-src 'self'; script-src 'self' 'nonce-<nonce>'; style-src 'self'; connect-src 'self';
object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

- **Scripts and styles are files of the application.** A page carries no `<style>` element, no `style` attribute, no event handler such as `onclick`, and no script of its own: a browser would refuse each. What a page needs of them goes into a file under `wwwroot`, or beside its component.
- **The one script that is written into a page is the framework's import map.** It carries the nonce: 16 random bytes, new for every answer, so text that reaches a page cannot know it. `App.razor` reads it with `BrowserDefenses.NonceOf`.
- **One place says it.** By itself the framework says `X-Frame-Options: SAMEORIGIN` on a page with a form, and `frame-ancestors 'self'` on a page. Both are turned off, in `TransportSecurity` and in `WebUiModule`, and the headers above are the last word on every answer.
- **In the Development environment** `connect-src` also names `ws://localhost:*` and `wss://localhost:*`, which `dotnet watch` connects to when it reloads a page.

`BrowserDefenseTests` holds each kind of answer to the four headers. It also opens every page of the application, as a visitor and as a user, and fails for one that carries what the policy refuses. The pages are found by their routes, so a page that is added later is among them.

### Injection

| Rule | How |
|---|---|
| What a request brings reaches the database as a value, and never as part of a statement. | Every component asks through `EcnanibDbContext`. A query is written in LINQ. The few statements that are written as SQL use `ExecuteSqlAsync` and `SqlQuery`, which take an interpolated string and send each value in it as a parameter. |
| No code sends SQL that was put together as text. | `ExecuteSqlRaw`, `SqlQueryRaw`, and `FromSqlRaw` are among the methods that no code may call, in [`ComponentRules.cs`](../tests/Ecnanib.Tests/Architecture/ComponentRules.cs). |
| No component writes a database command of its own. | `System.Data` and the driver are reserved for `Platform`, in the same file. The statements that `Platform` sends itself, for its two locks, are fixed text with parameters. |
| Nothing a request brings becomes a line of an email. | An email address with a space or a control character in it is not one, for registration, sign-in, and password reset alike. See "Registering" in [accounts.md](accounts.md). |

The application starts no process, and builds no file path from a request. `InjectionTests` registers, signs in, and asks for a reset link with SQL in the address, in the password, and in the token of a link: each is the text it is.

### Cross-site scripting

| Rule | How |
|---|---|
| A page writes what a person typed as text. | Razor encodes everything a page writes. The one way around it is a `MarkupString`, and every member of it is among the methods that no code may call, in `ComponentRules.cs`. |
| A script that reaches a page all the same is not run. | The policy: no script in a page without the nonce, none from another site, no event handler, and no text run as code. |
| A script could not read a cookie. | Every cookie is `HttpOnly`. See "Cookies". |
| A link cannot lead a user from the application to another site. | `returnUrl` is followed only when it is a path of this application. See "Signing in" in [accounts.md](accounts.md). |
| What the application serves is not taken for a page. | `nosniff`, and the export of a user's data is sent as a file to keep. |

`BrowserDefenseTests` types markup into the sign-in form, and registers an address that holds markup: an address may. Both come back as text, on the form and in the bar that names the user.

### Request forgery

| Rule | How |
|---|---|
| A form is taken only with the token of a page that the application served to the same browser. | Every form carries `<AntiforgeryToken />`. The token is in two halves, one in the form and one in the cookie `__Host-ecnanib-antiforgery`, and it is made for the user the page showed. The framework refuses a form that is posted to a page without it, with status 400, before the page sees the form. |
| An endpoint that is not a page has the check, or says why it need not. | [`ForgeryRules.cs`](../tests/Ecnanib.Tests/Security/ForgeryRules.cs) lists the endpoints that take more than a read without the framework's check, each with why. Today they are `/sign-out`, which checks the token itself for a request that is signed in, and the framework's connection of an interactive page. |
| A request that another site makes does not carry the session. | The session cookie is `SameSite=Lax`. A browser sends it when the user follows a link from another site, and not with a form that another site posts or a connection that it opens. |
| Opening a link does nothing that a user would have to ask for. | The link of an email shows a page with a button, and the button is a form. See "Verifying the address" in [accounts.md](accounts.md). |

`RequestForgeryTests` posts each form that a visitor can post, without the token and with a wrong one, and with the token of another browser: nothing is stored and nothing is sent. The forms of signed-in users have such a test beside what they do. A test holds the endpoints to `ForgeryRules`: an endpoint that is added later, and takes a post without the check, fails it until it is listed.

### Clickjacking

| Rule | How |
|---|---|
| No site may show the application in a frame, this one included. | `frame-ancestors 'none'` in the policy and `X-Frame-Options: DENY`, on every answer. The application shows none of its own pages in a frame. |

`BrowserDefenseTests` asks for each kind of answer, a page with a form among them, and finds each header once.

## Stored secrets

A secret that the application must be able to read again is stored encrypted (NFR-SEC-5). `StoredSecrets` in `Platform/Security` does it, for every component, with the master keys of the configuration file. A secret that only needs to be recognised, such as a password or the token of a session, is not encrypted but hashed: see [accounts.md](accounts.md).

| Rule | How |
|---|---|
| Authenticated encryption with a 256-bit key (FR-KEY-6). | AES-256-GCM. A value that was changed, in any bit, does not decrypt. |
| A nonce for every value (FR-KEY-6). | 12 random bytes, new each time. The same secret is stored differently every time it is stored. |
| A value names the master key that encrypted it (FR-KEY-6). | `EncryptedSecret` is the value and the id of the key. A component stores both: the id in a column of its own, beside the value. |
| New values use the active key, and earlier values still read (FR-KEY-7). | `Security:ActiveKeyId` names the key that encrypts. A value is decrypted with the key it names, so it reads for as long as `Security:MasterKeys` lists that key. Ids ignore case. |
| A value belongs to one thing. | Whoever encrypts says what the value is bound to, such as `two-factor:<user id>`, and must say the same to decrypt. A value that is copied to another row does not decrypt there. |
| Decrypted only for the call that needs it (FR-KEY-9). | `Decrypt` hands the secret to its caller, who uses it and clears it. Nothing keeps it. |

A stored value is the nonce, the ciphertext, and the tag of 16 bytes, in that order.

What is encrypted this way today is the key of two-factor authentication, in `users.two_factor_secret`. See "The key" in [accounts.md](accounts.md).

**To change the master key,** add a key to `Security:MasterKeys` under a new id, name it in `Security:ActiveKeyId`, and keep the earlier key listed. Values that are stored from then on use the new key. **Removing a key from the file makes every value it encrypted unreadable:** a user whose two-factor key is one of them cannot sign in, and the request ends in an error.

Not built yet: the routine that encrypts every stored value again with the active key, after which an earlier key can be removed, and the encryption of users' API keys.

## The request pipeline

The order, in `Program.cs`:

1. What the browser is told to refuse: the headers of every answer. See "What every answer tells the browser".
2. The cookie policy.
3. Request correlation: the `RequestId` of every log event of the request. See [logging.md](logging.md).
4. Authentication: who is signed in, from the session cookie.
5. User correlation: the `UserId` of every log event of the request.
6. Error handling.
7. The redirect of plain HTTP.
8. Authorization: a page for signed-in users sends a visitor to sign in, and a page for operators refuses a user who is not one. See [ownership.md](ownership.md).
9. The check of the token against request forgery.
10. The endpoints.

Authentication is ahead of the error handling so that an error is logged with the user it happened to. It therefore must not fail a request itself. See "How a request is signed in" in [accounts.md](accounts.md).
