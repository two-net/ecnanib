# Configuration

Every application setting, secrets included, comes from one file: `appsettings.json` in the content root. The application reads its settings from nowhere else, and it refuses to start when the file is invalid (NFR-CFG-1 to -6).

The rules below are enforced by tests in `tests/Ecnanib.Tests/Settings` and `tests/Ecnanib.Tests/Architecture`.

## The file

| File | In source control | Purpose |
|---|---|---|
| `src/Ecnanib/appsettings.template.json` | Yes | Every key, with the defaults from the specification and a `<placeholder>` wherever a deployment supplies its own value. |
| `appsettings.json` | No | The real file. It holds the database password and the master encryption keys. |

The real file is git-ignored, and the build never copies it to `bin/` or into a published application. The template is published next to the application.

### Creating it

```
cd src/Ecnanib
cp appsettings.template.json appsettings.json
chmod 600 appsettings.json
openssl rand -base64 32
```

Replace each `<placeholder>`. The last command prints a master key for `Security:MasterKeys`. In a deployment the file sits in the folder the application runs from.

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

- **Every key is required**, except `Email:User` and `Email:Password`, each provider's `CatalogKey`, and the two entries of `Logging:Outputs`. Lists may be empty unless a rule says otherwise. There are no defaults in code: the defaults are the values in the template.
- **Unknown keys are errors.** A misspelled key is reported instead of ignored. This also keeps user preferences, agent settings, and runtime flags such as the global halt out of the file. They belong in the database (NFR-CFG-6).
- **Types are those of JSON.** A number is written as a number, not as text.
- **A placeholder is an error.** A value still in the form `<...>` has not been filled in.
- As in any appsettings file, keys ignore letter case, and comments and trailing commas are allowed.
- A change to the file takes effect at the next start.

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

A platform variable cannot carry a section that belongs to the configuration file. `ASPNETCORE_Logging__LogLevel__Default`, for example, is dropped: the built-in logger reads the host's configuration, so without this rule the variable would set log levels. Sections of the platform's own, such as `Kestrel`, are untouched.

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
2. Add it to `appsettings.template.json` with its default, or a placeholder if each deployment supplies it.
3. Add it to your own `appsettings.json`.

`TemplateTests` fails when the template and the records disagree.
