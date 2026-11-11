# Configuration

Every application setting, secrets included, comes from one file: `appsettings.json` in the content root. The application reads its settings from nowhere else, and it refuses to start when the file is invalid (NFR-CFG-1 to -7).

The rules below are enforced by tests in `tests/Ecnanib.Tests/Settings` and `tests/Ecnanib.Tests/Architecture`.

## The file

| File | In source control | Purpose |
|---|---|---|
| `src/Ecnanib/appsettings.json` | Yes | Every key, with the defaults from the specification and values for development on one machine: a local database, a local mail server, and a master key that protects nothing real. |
| `appsettings.json` of a deployment | No | The same keys with the deployment's own values. It holds the real database password and master encryption keys. |

The build never copies the tracked file to `bin/` or into a published application, so its development values cannot reach a deployment by accident.

### After cloning

```
chmod 600 src/Ecnanib/appsettings.json
```

Git checks the file out readable by other accounts, and the application refuses that (see Permissions). The command is needed once.

The file names a local database, which the application migrates when it starts, and a local mail server, which takes the email the application sends and delivers it nowhere. Start both with `docker compose up -d`. See [persistence.md](persistence.md) and [accounts.md](accounts.md).

### For a deployment

Put a copy of the tracked file in the folder the application runs from and replace its development values:

```
chmod 600 appsettings.json
openssl rand -base64 32
```

The last command prints a master key for `Security:MasterKeys`. Never deploy the tracked master key.

The `Email` section has no key for encryption: the port and the sign-in decide it. A mail server that takes a sign-in must offer STARTTLS, or listen on port 465. See "Email" in [accounts.md](accounts.md).

### Permissions

On Linux and macOS the application does not start while the group or other accounts have any access to the file. Only the account that runs the application may read it: `chmod 600 appsettings.json`.

Windows has no equivalent permission bits, so the application does not check there. Restrict the file's access list to the account that runs the application.

## Validation at startup

The file is read once, before anything else starts. It is checked for required keys, types, ranges, and key lengths. On any error the application writes every wrong key to standard error and exits with code 1:

```
Ecnanib cannot start.
The configuration file /srv/ecnanib/appsettings.json is invalid:
  Security:MasterKeys:key-1: must be 32 random bytes in Base64, which is a 256-bit key
  Email:Port: must be a whole number from 1 to 65535
  Limits:MaxAgents: is not a known setting
```

A report names keys and rules. It never prints a value, secret or not.

- **Every key is required**, except `Email:User` and `Email:Password`, each provider's `CatalogKey`, and the two entries of `Logging:Outputs`. Lists may be empty unless a rule says otherwise. There are no defaults in code: the defaults are the values in the tracked file.
- **Unknown keys are errors.** A misspelled key is reported instead of ignored. This also keeps user preferences, agent settings, and runtime flags such as the global halt out of the file. They belong in the database (NFR-CFG-6).
- **Types are those of JSON.** A number is written as a number, not as text.
- **A placeholder is an error.** A value in the form `<...>` has not been filled in.
- As in any appsettings file, keys ignore letter case, and comments and trailing commas are allowed.
- A change to the file takes effect at the next start, except for log levels.

## Changes while the application runs

`Logging:MinimumLevel` and `Logging:Overrides` take effect within a few seconds of saving the file, with no restart (NFR-CFG-7). The application logs the levels now in force. Every other key, the log outputs included, is read at the next start.

The whole file is validated again when it changes. If it is invalid, the levels stay as they were, and the application logs a warning that names the wrong keys and rules, never a value. The next valid save applies. See [logging.md](logging.md).

The rules for each key are next to its section in `src/Ecnanib/Platform/Settings`.

## What is not a source of settings

The default ASP.NET Core host layers many configuration sources. `PlatformModule.CreateBuilder` removes all of them except the platform's own:

| Source | Effect on settings |
|---|---|
| Environment variables | None |
| Command-line arguments | None |
| `appsettings.{Environment}.json` and other override files | None. There are no per-environment variants. |
| User secrets, a vault, or any other secret store | None. No such source is added. |

What the runtime platform defines for itself keeps its default behavior (NFR-CFG-3): variables with the `ASPNETCORE_` and `DOTNET_` prefixes, such as `ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS`, and the host switches `--environment` and `--contentRoot`. They decide how the host runs. They hold no application setting, and the environment name does not select a file. The application defines no variable of its own.

Settings never enter the host's `IConfiguration`. They are read from the file into `EcnanibSettings`, so nothing in the host's configuration can override them, and a dump of that configuration holds no secret.

A platform variable cannot carry a section that belongs to the configuration file. `ASPNETCORE_Logging__LogLevel__Default`, for example, is dropped: the host's logger reads the host's configuration, so without this rule the variable would reach the log levels. Sections of the platform's own, such as `Kestrel`, are untouched.

The connection string is in `ConnectionStrings`, under the name of the database context, where a .NET application keeps it. Other .NET applications also take that section from variables such as `ConnectionStrings__EcnanibDbContext`. Here such a variable has no effect, like any other: the connection string comes from the file.

## Using a setting

A component takes the section it needs as a constructor parameter:

```csharp
internal sealed class RateLimiter(BinanceSettings binance)
{
    private readonly int _share = binance.LimitSharePercent;
}
```

Two architecture rules keep this the only way:

- Only `Platform` may use `Microsoft.Extensions.Configuration`.
- No code may read the process environment or the command line directly.

Secrets are of type `Secret`, which prints as `***`. Call `Reveal()` at the point of use.

## Adding a setting

1. Add it to the section's record in `src/Ecnanib/Platform/Settings` and read it in that record's `Read` method, with its rule.
2. Add it to `src/Ecnanib/appsettings.json` with its default, or a value for development if each deployment supplies its own.
3. Add it to the file of each deployment.

`TrackedFileTests` fails when the tracked file and the records disagree.
