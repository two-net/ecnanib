using Ecnanib.Kernel;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>Logs a canary with a marker in the same event.</summary>
internal delegate void Emit(ILogger logger, string marker);

/// <summary>A way a secret could reach a log. The canary must never arrive, and the marker must.</summary>
internal sealed record CanaryCase(string Name, string Canary, Emit Emit);

/// <summary>
/// Every class of secret in FR-LOG-05, crossed with every route into a log event, plus the secrets
/// that are recognised by shape or by type. Canaries are purely alphanumeric, so a rule that stops
/// at a separator can't leave half of one behind while a search for the whole of it passes. They
/// stay under 64 characters, or the rule for long keys would mask them whatever else went wrong.
/// </summary>
internal static class CanaryCases
{
    private static readonly SecretClass[] Classes =
    [
        new("Secret", "ClientSecret", "client_secret", canary => new { ClientSecret = canary }),
        new("ApiKey", "ApiKey", "X-MBX-APIKEY", canary => new { ApiKey = canary }),
        new("Password", "Password", "password", canary => new { Password = canary }),
        new("TotpCode", "TotpCode", "totp", canary => new { TotpCode = canary }),
        new("Token", "AccessToken", "access_token", canary => new { AccessToken = canary }),
        new("Signature", "Signature", "signature", canary => new { Signature = canary }),
        new("AuthorizationHeader", "Authorization", "Authorization", canary => new { Authorization = canary }),
    ];

    private static readonly (string Name, Func<SecretClass, string, Emit> Create)[] Routes =
    [
        ("NamedProperty", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{{secret.Property}}}", canary)),

        ("Destructured", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{@Value}}", secret.Holder(canary))),

        ("Nested", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{@Value}}", new { Outer = new { Inner = secret.Holder(canary) } })),

        ("DestructuredSequence", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{@Value}}", new object[] { secret.Holder(canary) })),

        ("Dictionary", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{Value}}", new Dictionary<string, string> { [secret.Wire] = canary })),

        ("DestructuredPairs", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{@Value}}", new[] { new KeyValuePair<string, string>(secret.Wire, canary) })),

        ("RenderedPairs", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{Value}}", new[] { new KeyValuePair<string, string>(secret.Wire, canary) })),

        ("Sequence", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{Value}}", (object)new[] { $"{secret.Wire}={canary}" })),

        ("ScopeProperty", (secret, canary) => (logger, marker) =>
        {
            using (logger.BeginScope(new Dictionary<string, object?> { [secret.Property] = canary }))
            {
                logger.LogError("{Marker}", marker);
            }
        }),

        ("ScopeMessage", (secret, canary) => (logger, marker) =>
        {
            using (logger.BeginScope($"{secret.Wire}: {canary}"))
            {
                logger.LogError("{Marker}", marker);
            }
        }),

        ("TemplateText", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {secret.Wire}={canary}")),

        ("TokenAfterName", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {secret.Wire}: {{Value}}", canary)),

        ("Uri", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{Value}}", new Uri($"https://api.example.com/v3?{secret.Wire}={canary}"))),

        ("Stringified", (secret, canary) => (logger, marker) =>
            logger.LogError($"{marker} {{Value}}", new Rendered($"{secret.Wire}: {canary}"))),

        ("ExceptionMessage", (secret, canary) => (logger, marker) =>
            logger.LogError(new InvalidOperationException($"Failed with {secret.Wire}={canary}"), "{Marker}", marker)),

        ("InnerException", (secret, canary) => (logger, marker) =>
            logger.LogError(
                new InvalidOperationException("Outer", new ArgumentException($"{secret.Wire}: {canary}")),
                "{Marker}",
                marker)),
    ];

    /// <summary>Logged under an innocent name, so only the value's shape can give them away.</summary>
    private static readonly (string Name, Func<string, string> Render)[] Shapes =
    [
        ("AnthropicKey", canary => $"sk-ant-api03-{canary}"),
        ("OpenAiKey", canary => $"sk-proj-{canary}"),
        ("XaiKey", canary => $"xai-{canary}"),
        ("GoogleKey", canary => $"AIzaSy{canary}"),
        ("Jwt", canary => $"eyJhbGciOiJIUzI1NiJ9.eyJzdWIi{canary}.signature"),
        ("BearerAnywhere", canary => $"sent Bearer {canary} upstream"),
        ("BasicAuthHeader", canary => $"Authorization: Basic {canary}"),
        ("CookieHeader", canary => $"Cookie: theme=dark; session={canary}"),
        ("UrlUserInfo", canary => $"postgres://admin:{canary}@db.example.com/ecnanib"),
        ("ConnectionString", canary => $"Host=db;Username=app;Password={canary};Database=ecnanib"),
        ("JsonBody", canary => $$"""{"password":"{{canary}}","user":"someone"}"""),
        ("QuotedWithSpaces", canary => $$"""{"secret":"correct horse {{canary}}"}"""),
        ("EscapedJson", canary => $$"""{\"token\":\"{{canary}}\"}"""),
        ("ReceiveWindowQuery", canary => $"timestamp=1&recvWindow=5000&signature={canary}&x=1"),
        ("Ed25519PrivateKey", canary => $"-----BEGIN PRIVATE KEY-----\nMC4CAQAwBQYDK2VwBCIEI{canary}\n-----END PRIVATE KEY-----"),
        ("Ed25519PrivateKeyCutShort", canary => $"pasted -----BEGIN PRIVATE KEY----- MC4CAQAwBQYDK2VwBCIEI{canary}"),
        ("EmailedLink", canary => $"https://ecnanib.test/reset-password?token={canary}"),
        ("LinkThroughSignIn", canary => $"/sign-in?returnUrl=%2Faccount%2Fverify-email%3Ftoken%3D{canary}"),
        ("LinkThroughSignInLowercase", canary => $"/sign-in?returnUrl=%2faccount%2fverify-email%3ftoken%3d{canary}%26x%3d1"),
    ];

    public static IReadOnlyList<CanaryCase> All { get; } =
    [
        .. from secret in Classes
           from route in Routes
           let canary = NewCanary()
           select new CanaryCase($"{secret.Name}.{route.Name}", canary, route.Create(secret, canary)),

        .. from shape in Shapes
           let canary = NewCanary()
           select new CanaryCase(
               $"Shape.{shape.Name}",
               canary,
               (logger, marker) => logger.LogError($"{marker} {{Value}}", shape.Render(canary))),

        .. DecryptedSecrets(),

        BinanceKey(),
        NumericTotpCode(),
        KeysThatMaskToTheSameText(),
        HttpRequest("HttpRequest.Destructured", "{@Value}"),
        HttpRequest("HttpRequest.Stringified", "{Value}"),
        HttpRequestWithUnrecognisedHeader(),
    ];

    /// <summary>Every name a secret goes by here, as a property and on the wire. The audit trail masks fields by these.</summary>
    public static IReadOnlyList<string> SecretNames { get; } = [.. Classes.SelectMany(secret => new[] { secret.Property, secret.Wire })];

    /// <summary>Secrets that only their shape gives away, each rendered around a canary.</summary>
    public static IReadOnlyList<(string Name, Func<string, string> Render)> SecretShapes => Shapes;

    public static string NewCanary() => $"Z{Guid.NewGuid():N}";

    /// <summary>
    /// A decrypted secret under a name no rule knows, so only its type keeps it out of the logs
    /// (FR-KEY-09). See Masking in docs/architecture.md.
    /// </summary>
    private static IEnumerable<CanaryCase> DecryptedSecrets()
    {
        (string Name, Action<ILogger, string, SecretValue> Emit)[] routes =
        [
            ("Property", (logger, marker, secret) => logger.LogError($"{marker} {{Value}}", secret)),
            ("Destructured", (logger, marker, secret) => logger.LogError($"{marker} {{@Value}}", secret)),
            ("Nested", (logger, marker, secret) => logger.LogError($"{marker} {{@Value}}", new { Outer = new { Inner = secret } })),
            ("Sequence", (logger, marker, secret) => logger.LogError($"{marker} {{Value}}", (object)new object[] { secret })),
            ("Interpolated", (logger, marker, secret) => logger.LogError(new InvalidOperationException($"Failed with {secret}"), "{Marker}", marker)),
            ("ScopeProperty", (logger, marker, secret) =>
            {
                using (logger.BeginScope(new Dictionary<string, object?> { ["Value"] = secret }))
                {
                    logger.LogError("{Marker}", marker);
                }
            }),
        ];

        foreach (var (name, emit) in routes)
        {
            var canary = NewCanary();

            yield return new CanaryCase($"SecretValue.{name}", canary, (logger, marker) =>
            {
                using var secret = SecretValue.FromString(canary);
                emit(logger, marker, secret);
            });
        }
    }

    /// <summary>Binance keys and signatures are 64 characters with no marker at all.</summary>
    private static CanaryCase BinanceKey()
    {
        var canary = (NewCanary() + NewCanary())[..64];

        return new CanaryCase(
            "Shape.BinanceKey",
            canary,
            (logger, marker) => logger.LogError($"{marker} {{Value}}", canary));
    }

    /// <summary>A dictionary can't hold the same key twice, and masking must not cost the event for that.</summary>
    private static CanaryCase KeysThatMaskToTheSameText()
    {
        var canary = NewCanary();

        return new CanaryCase(
            "Dictionary.KeysThatMaskToTheSameText",
            canary,
            (logger, marker) => logger.LogError(
                $"{marker} {{Value}}",
                new Dictionary<string, string> { [$"sk-ant-api03-{canary}a"] = "one", [$"sk-ant-api03-{canary}b"] = "two" }));
    }

    /// <summary>A TOTP code can be a number, and only token counts are let through as numbers.</summary>
    private static CanaryCase NumericTotpCode()
    {
        var code = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);

        return new CanaryCase(
            "Numeric.TotpCode",
            code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (logger, marker) => logger.LogError($"{marker} {{TotpCode}}", code));
    }

    private static CanaryCase HttpRequest(string name, string placeholder)
    {
        var canary = NewCanary();

        return new CanaryCase(name, canary, (logger, marker) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.example.com/v3?signature={canary}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", canary);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {canary}");

            logger.LogError($"{marker} {placeholder}", request);
        });
    }

    /// <summary>
    /// No rule can tell this header holds a secret. Only the destructuring policy stops it, by never
    /// taking the request apart. Without the @ it would render through ToString(), which is a known
    /// limit: see Masking in docs/architecture.md.
    /// </summary>
    private static CanaryCase HttpRequestWithUnrecognisedHeader()
    {
        var canary = NewCanary();

        return new CanaryCase("HttpRequest.UnrecognisedHeader", canary, (logger, marker) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v3");
            request.Headers.TryAddWithoutValidation("X-Reference", canary);

            logger.LogError($"{marker} {{@Value}}", request);
        });
    }

    private sealed record SecretClass(string Name, string Property, string Wire, Func<string, object> Holder);

    /// <summary>An object that logs through its ToString(), as a record or a type with no @ does.</summary>
    private sealed record Rendered(string Text)
    {
        public override string ToString() => Text;
    }
}
