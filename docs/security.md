# Security

How requests travel between a browser and the application: over TLS, with cookies that are sent only over TLS and that scripts cannot read (NFR-SEC-1).

The options are in `src/Ecnanib/Platform/Security`, and the order of the request pipeline is in `src/Ecnanib/Program.cs`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Security` and `tests/Ecnanib.Tests/Accounts`.

Sign-in, sessions, and password reset are in [accounts.md](accounts.md). Rate limits and the other defenses of a web application are a separate feature and are not built yet.

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

The application then believes the `X-Forwarded-Proto` and `X-Forwarded-For` headers of every request, whoever sent it. **Only the proxy may be able to reach the application's port.** Anyone else who can reach it can claim that a plain request arrived over TLS.

## Cookies

Every cookie the application sets is `Secure` and `HttpOnly`, and at least `SameSite=Lax`. One place enforces it, the cookie policy at the start of the pipeline, so it holds for a cookie that a later feature or the framework adds.

| Cookie | Purpose | Attributes |
|---|---|---|
| `__Host-ecnanib-session` | The token of a session. See "Sessions" in [accounts.md](accounts.md). | `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, no expiry |
| `__Host-ecnanib-antiforgery` | One half of the token against request forgery that every form carries. | `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no expiry |

The `__Host-` prefix makes a browser refuse a cookie unless it was set over TLS, for the whole host, and without a domain. So a site on another subdomain of the same domain cannot plant either cookie.

Because the cookies are `Secure`, a browser does not send them over plain HTTP. The tests speak TLS to the application for that reason.

## The request pipeline

The order, in `Program.cs`:

1. The cookie policy.
2. Request correlation: the `RequestId` of every log event of the request. See [logging.md](logging.md).
3. Authentication: who is signed in, from the session cookie.
4. User correlation: the `UserId` of every log event of the request.
5. Error handling.
6. The redirect of plain HTTP.
7. Authorization: a page for signed-in users sends a visitor to sign in.
8. The check of the token against request forgery.
9. The endpoints.

Authentication is ahead of the error handling so that an error is logged with the user it happened to. It therefore must not fail a request itself. See "How a request is signed in" in [accounts.md](accounts.md).
