using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Secrets, API keys, tokens, and passwords never appear in logs. Whatever way one is passed to
/// the logger, it reaches no output: not the console and not the file (NFR-LOG-4).
/// </summary>
[Collection(ConsoleCollection.Name)]
public class MaskingTests
{
    private const string Secret = "s3cr3t-MARKER-9f8e7d6c5b4a";

    // Logged next to the secret, to prove the event itself did reach the outputs.
    private const string Canary = "canary-in-the-same-event";

    /// <summary>Each row is how a secret gets to the logger, and the call that passes it.</summary>
    public static TheoryData<string, Action<ILogger>> WaysToLogASecret => new()
    {
        {
            "as a property named for what it is",
            logger => logger.LogWarning("Calling {Canary} with {ApiKey}", Canary, Secret)
        },
        {
            "as a secret from the configuration file",
            logger => logger.LogWarning("Calling {Canary} with {Value}", Canary, new Secret(Secret))
        },
        {
            "as a member of a logged object",
            logger => logger.LogWarning("Signing in {Canary} {@Login}", Canary, new { User = "ada", Password = Secret })
        },
        {
            "deep inside a logged object, among headers",
            logger => logger.LogWarning(
                "Sending {Canary} {@Request}",
                Canary,
                new
                {
                    Url = "https://exchange.example/order",
                    Headers = new Dictionary<string, string> { ["X-MBX-APIKEY"] = Secret, ["Accept"] = "application/json" },
                    Parts = new[] { new { api_secret = Secret } },
                })
        },
        {
            "as an entry of a logged dictionary",
            logger => logger.LogWarning(
                "Headers {Canary} {Headers}", Canary, new Dictionary<string, string> { ["Authorization"] = Secret })
        },
        {
            "as a property of a scope",
            logger =>
            {
                using (logger.BeginScope(new Dictionary<string, object> { ["AccessToken"] = Secret }))
                {
                    logger.LogWarning("Inside {Canary}", Canary);
                }
            }
        },
        {
            "inside an authorization header in text",
            logger => logger.LogWarning("Sent {Canary} {Header}", Canary, $"Authorization: Bearer {Secret}")
        },
        {
            "inside the query of an address in text",
            logger => logger.LogWarning(
                "Called {Canary} {Address}", Canary, $"https://exchange.example/order?symbol=BTCUSDT&signature={Secret}&timestamp=1")
        },
        {
            "inside the query of an address",
            logger => logger.LogWarning(
                "Called {Canary} {Address}", Canary, new Uri($"https://ai.example/v1/models?key={Secret}"))
        },
        {
            "inside a connection string in text",
            logger => logger.LogWarning("Connecting {Canary} {Target}", Canary, $"Host=db;Username=app;Password={Secret};Database=x")
        },
        {
            "inside a JSON body in text",
            logger => logger.LogWarning("Body {Canary} {Body}", Canary, $$"""{"symbol":"BTCUSDT","apiKey":"{{Secret}}"}""")
        },
        {
            "inside a message written without a template",
#pragma warning disable CA2254 // The point of the case: the message is built, not a template.
            logger => logger.LogWarning($"{Canary} used token={Secret}")
#pragma warning restore CA2254
        },
        {
            "inside the message of an exception",
            logger => logger.LogError(
                new InvalidOperationException($"Sign-in failed for Username=app;Password={Secret}"), "Failed {Canary}", Canary)
        },
        {
            "inside the message of an inner exception",
            logger => logger.LogError(
                new InvalidOperationException("Outer", new FormatException($"Bad header 'Authorization: Basic {Secret}'")),
                "Failed {Canary}",
                Canary)
        },
    };

    [Theory]
    [MemberData(nameof(WaysToLogASecret))]
    public void A_secret_passed_to_the_logger_reaches_no_output(string way, Action<ILogger> log)
    {
        using var console = new ConsoleCapture();
        using var application = new LoggedApplication(LoggedApplication.Settings());

        log(application.Logger());

        foreach (var (output, text) in new[] { ("file", application.LogText()), ("console", console.Text) })
        {
            Assert.True(text.Contains(Canary), $"The event did not reach the {output}, so the test proves nothing ({way}).");
            Assert.False(text.Contains(Secret), $"A secret passed {way} reached the {output}.");
            Assert.True(text.Contains("***"), $"The secret passed {way} was dropped from the {output}, not masked.");
        }
    }

    [Fact]
    public void A_secret_of_the_configuration_file_is_masked_by_its_value_under_any_name()
    {
        using var console = new ConsoleCapture();
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var settings = application.Services.GetService(typeof(EcnanibSettings)) as EcnanibSettings;
        Assert.NotNull(settings);

        var secrets = new[]
        {
            settings.ConnectionStrings.EcnanibDbContext.Reveal(),
            settings.Security.MasterKeys["k1"].Reveal(),
            settings.Security.MasterKeys["k2"].Reveal(),
            settings.Email.Password!.Reveal(),
            settings.AiProviders.ByCode.Values.Select(provider => provider.CatalogKey).OfType<Secret>().Single().Reveal(),
        };

        var logger = application.Logger();
        foreach (var secret in secrets)
        {
            // No name and no shape gives these away: only their value does.
            logger.LogWarning("Read {Canary} {Value} from {Place}", Canary, secret, $"the file ({secret})");
        }

        // The settings themselves, logged whole.
        logger.LogWarning("Started {Canary} with {@Settings}", Canary, settings);

        foreach (var text in new[] { application.LogText(), console.Text })
        {
            Assert.Contains(Canary, text);
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, text));
            Assert.DoesNotContain("database-password", text);
        }
    }

    [Fact]
    public void What_is_not_a_secret_is_written_as_it_is()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());

        application.Logger().LogInformation(
            "Used {InputTokens} and {OutputTokens} tokens, encrypted with key {ActiveKeyId}, for {Symbol}",
            120, 45, "k2", "BTCUSDT");

        var written = Assert.Single(application.Events(), e => e.Has("InputTokens"));
        Assert.Equal(120, written["InputTokens"].GetInt32());
        Assert.Equal(45, written["OutputTokens"].GetInt32());
        Assert.Equal("k2", written.Text("ActiveKeyId"));
        Assert.Equal("BTCUSDT", written.Text("Symbol"));
        Assert.DoesNotContain("***", written.ToString());
    }
}
