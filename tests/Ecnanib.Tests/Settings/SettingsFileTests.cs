using System.Net;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// The configuration file is read in full and validated for required keys, types, ranges, and key
/// lengths. Every wrong key is reported, and no value is (NFR-CFG-1, NFR-CFG-4).
/// </summary>
public class SettingsFileTests
{
    private const string Marker = "SECRET-MARKER";

    [Fact]
    public void Every_setting_in_the_outline_is_read_from_the_file()
    {
        var settings = Load(SettingsJson.Valid());

        Assert.Equal("Ecnanib Test", settings.Application.Name);
        Assert.Equal(new Uri("https://ecnanib.example"), settings.Application.PublicUrl);
        Assert.False(settings.Application.RegistrationOpen);
        Assert.Equal(["operator@ecnanib.example"], settings.Application.Operators);

        Assert.Equal(
            "Host=db.example;Database=ecnanib;Username=app;Password=database-password",
            settings.Database.ConnectionString.Reveal());

        Assert.Equal("k2", settings.Security.ActiveKeyId);
        Assert.Equal(["k1", "k2"], settings.Security.MasterKeys.Keys.Order());
        Assert.Equal(SettingsJson.MasterKey, settings.Security.MasterKeys["K2"].Reveal());
        Assert.Equal(45, settings.Security.SessionIdleMinutes);

        Assert.Equal(LogLevel.Warning, settings.Logging.MinimumLevel);
        Assert.Equal(LogLevel.Error, settings.Logging.Overrides["Microsoft.AspNetCore"]);
        Assert.True(settings.Logging.Outputs.Console);
        Assert.Equal(new LogFileSettings("logs/test.log", LogFileRolling.Day, 7), settings.Logging.Outputs.File);

        Assert.Equal("mail.example", settings.Email.Host);
        Assert.Equal(2525, settings.Email.Port);
        Assert.Equal("mailer", settings.Email.User);
        Assert.Equal("email-password", settings.Email.Password?.Reveal());
        Assert.Equal("noreply@ecnanib.example", settings.Email.From);

        Assert.Equal(
            [new AiProviderCode("claude"), new AiProviderCode("gemini"), new AiProviderCode("gpt"), new AiProviderCode("grok")],
            settings.AiProviders.ByCode.Keys.OrderBy(code => code.Value));
        var claude = settings.AiProviders.ByCode[new AiProviderCode("Claude")];
        Assert.Equal(new Uri("https://claude.example"), claude.BaseUrl);
        Assert.Equal("claude-catalog-key", claude.CatalogKey?.Reveal());
        Assert.Equal(30, claude.TimeoutSeconds);
        Assert.Equal(["claude-*"], claude.Allow);
        Assert.Equal(["*-preview"], claude.Deny);
        Assert.Empty(claude.LiveSearch);
        var grok = settings.AiProviders.ByCode[new AiProviderCode("Grok")];
        Assert.Null(grok.CatalogKey);
        Assert.Equal(["grok-*"], grok.LiveSearch);

        Assert.Equal(new CatalogSyncSettings(12, 3), settings.CatalogSync);

        Assert.Equal(new Uri("https://spot-live.example"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Live));
        Assert.Equal(new Uri("https://spot-demo.example"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Demo));
        Assert.Equal(new Uri("https://futures-live.example"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Live));
        Assert.Equal(new Uri("https://futures-demo.example"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Demo));
        Assert.Equal(["BTCUSDT", "SOLUSDT"], settings.Binance.Symbols);
        Assert.Equal([IPAddress.Parse("203.0.113.7")], settings.Binance.OutboundIps);
        Assert.Equal(60, settings.Binance.LimitSharePercent);
        Assert.Equal(4, settings.Binance.ReconcileMinutes);

        Assert.Equal(
            new DefaultRolesSettings(
                MarketAnalyst: new AiProviderCode("claude"),
                SentimentAnalyst: new AiProviderCode("grok"),
                Strategist: new AiProviderCode("gpt"),
                RiskManager: new AiProviderCode("gemini")),
            settings.Trading.DefaultRoles);
        Assert.Equal(
            new AggressiveSettings(
                RiskPerTradePercent: new Bounded<decimal>(0.25m, 1.5m, 4m),
                MinRewardToRisk: 1.75m,
                LeverageCap: new Bounded<int>(2, 4, 8),
                LiquidationBuffer: 2.5m,
                DailyLossPercent: new Bounded<decimal>(1m, 5m, 12m),
                DrawdownPercent: new Bounded<decimal>(8m, 20m, 40m),
                MaxEntryDistancePercent: new Bounded<decimal>(0.2m, 0.8m, 2m)),
            settings.Trading.Aggressive);
        Assert.Equal(new StaleSecondsSettings(100, 200, 400, 800), settings.Trading.StaleSeconds);

        Assert.Equal(new ExperienceSettings(4, 15, 25, new ExperienceBoardSettings(6, 60)), settings.Experience);
        Assert.Equal(new RetentionSettings(30, 180, 45, 365), settings.Retention);
        Assert.Equal(new LimitsSettings(7), settings.Limits);
    }

    /// <summary>Each row is a key to change, its new value as JSON, and the key that must be reported.</summary>
    public static TheoryData<string, string, string> WrongValues => new()
    {
        // Required keys.
        { "Database:ConnectionString", "\"\"", "Database:ConnectionString" },
        { "Database:ConnectionString", "null", "Database:ConnectionString" },
        { "Email", "null", "Email" },

        // Forms.
        { "Database:ConnectionString", "\"db.example\"", "Database:ConnectionString" },
        { "Database:ConnectionString", "\"Host=db.example;Port=five\"", "Database:ConnectionString" },
        { "Database:ConnectionString", "\"Host=db.example;Passwrod=database-password\"", "Database:ConnectionString" },

        // Types.
        { "Email:Port", "\"2525\"", "Email:Port" },
        { "Email:Port", "25.5", "Email:Port" },
        { "Application:Name", "7", "Application:Name" },
        { "Application:RegistrationOpen", "\"yes\"", "Application:RegistrationOpen" },
        { "Application:Operators", "\"operator@ecnanib.example\"", "Application:Operators" },
        { "Security:MasterKeys", "[]", "Security:MasterKeys" },
        { "Trading", "\"aggressive\"", "Trading" },
        { "Logging:Outputs:File", "true", "Logging:Outputs:File" },
        { "Trading:Aggressive:LeverageCap", "[1, 5.5, 10]", "Trading:Aggressive:LeverageCap:1" },

        // Ranges.
        { "Email:Port", "70000", "Email:Port" },
        { "Security:SessionIdleMinutes", "0", "Security:SessionIdleMinutes" },
        { "AiProviders:Claude:TimeoutSeconds", "601", "AiProviders:Claude:TimeoutSeconds" },
        { "Binance:LimitSharePercent", "101", "Binance:LimitSharePercent" },
        { "Trading:Aggressive:MinRewardToRisk", "0.9", "Trading:Aggressive:MinRewardToRisk" },
        { "Trading:Aggressive:LiquidationBuffer", "1.4", "Trading:Aggressive:LiquidationBuffer" },
        { "Trading:Aggressive:RiskPerTradePercent", "[0, 2, 5]", "Trading:Aggressive:RiskPerTradePercent:0" },
        { "Trading:Aggressive:DrawdownPercent", "[10, 25, 101]", "Trading:Aggressive:DrawdownPercent:2" },
        { "Trading:Aggressive:LeverageCap", "[1, 5, 126]", "Trading:Aggressive:LeverageCap:2" },
        { "Trading:Aggressive:RiskPerTradePercent", "[3, 2, 5]", "Trading:Aggressive:RiskPerTradePercent" },
        { "Trading:Aggressive:DailyLossPercent", "[2, 6]", "Trading:Aggressive:DailyLossPercent" },
        { "Experience:Board:MinUsers", "0", "Experience:Board:MinUsers" },
        { "Retention:AuditDays", "-1", "Retention:AuditDays" },
        { "Limits:MaxAgentsPerUser", "0", "Limits:MaxAgentsPerUser" },
        { "Logging:Outputs:File:RetainedFiles", "0", "Logging:Outputs:File:RetainedFiles" },

        // Key lengths.
        { "Security:MasterKeys:k1", "\"c2hvcnQ=\"", "Security:MasterKeys:k1" },
        { "Security:MasterKeys:k1", "\"not Base64 at all\"", "Security:MasterKeys:k1" },
        { "Security:MasterKeys", "{}", "Security:MasterKeys" },

        // Values that must fit another key or a format.
        { "Security:ActiveKeyId", "\"k9\"", "Security:ActiveKeyId" },
        { "Trading:DefaultRoles:Strategist", "\"Llama\"", "Trading:DefaultRoles:Strategist" },
        { "Email:Password", "\"\"", "Email:Password" },
        { "Email:From", "\"nobody\"", "Email:From" },
        { "Application:PublicUrl", "\"ecnanib.example\"", "Application:PublicUrl" },
        { "Application:PublicUrl", "\"ftp://ecnanib.example\"", "Application:PublicUrl" },
        { "Application:PublicUrl", "\"<the address>\"", "Application:PublicUrl" },
        { "Application:Operators", "[\"not an address\"]", "Application:Operators:0" },
        { "Application:Operators", "[\"a@ecnanib.example\", \"A@ecnanib.example\"]", "Application:Operators" },
        { "Logging:MinimumLevel", "\"Loud\"", "Logging:MinimumLevel" },
        { "Logging:Overrides", "{ \"Ecnanib\": \"Loud\" }", "Logging:Overrides:Ecnanib" },
        { "Logging:Overrides", "{ \"Ecnanib.*.Adapters.*\": \"Debug\" }", "Logging:Overrides:Ecnanib.*.Adapters.*" },
        { "Logging:Outputs:File:Rolling", "\"Hour\"", "Logging:Outputs:File:Rolling" },
        { "AiProviders", "{}", "AiProviders" },
        { "AiProviders:Claude:BaseUrl", "\"\"", "AiProviders:Claude:BaseUrl" },
        { "AiProviders:Claude:Allow", "[\"\"]", "AiProviders:Claude:Allow:0" },
        { "Binance:Spot:Demo", "{}", "Binance:Spot:Demo:BaseUrl" },
        { "Binance:Symbols", "[]", "Binance:Symbols" },
        { "Binance:Symbols", "[\"btcusdt\"]", "Binance:Symbols:0" },
        { "Binance:OutboundIps", "[\"300.1.1.1\"]", "Binance:OutboundIps:0" },
        { "Trading:StaleSeconds", "{ \"15m\": 100, \"1H\": 200, \"1D\": 800 }", "Trading:StaleSeconds:4H" },

        // Keys the application does not know. Runtime flags, user preferences, and agent settings
        // live in the database, so the file has no place for them (NFR-CFG-6).
        { "Limits:MaxAgentsPerUsers", "10", "Limits:MaxAgentsPerUsers" },
        { "Trading:StaleSeconds:1W", "3600", "Trading:StaleSeconds:1W" },
        { "Logging:Outputs:Console:Colors", "true", "Logging:Outputs:Console:Colors" },
        { "Application:GlobalHalt", "true", "Application:GlobalHalt" },
        { "Agents", "{ \"Default\": { \"Timeframe\": \"1H\" } }", "Agents" },
        { "Users", "[ { \"Theme\": \"dark\" } ]", "Users" },
    };

    [Theory]
    [MemberData(nameof(WrongValues))]
    public void A_wrong_key_is_the_one_reported(string key, string value, string reported)
    {
        var invalid = Assert.Throws<InvalidSettingsException>(() => Load(SettingsJson.Valid().WithJson(key, value)));

        Assert.Equal([reported], invalid.Problems.Select(problem => problem.Key));
    }

    [Fact]
    public void A_missing_key_is_reported()
    {
        var invalid = Assert.Throws<InvalidSettingsException>(
            () => Load(SettingsJson.Valid().Without("Retention:DecisionDays")));

        Assert.Equal([new SettingsProblem("Retention:DecisionDays", "is required")], invalid.Problems);
    }

    [Fact]
    public void A_missing_section_is_reported_once_and_not_for_each_key_in_it()
    {
        var invalid = Assert.Throws<InvalidSettingsException>(() => Load(SettingsJson.Valid().Without("Binance")));

        Assert.Equal([new SettingsProblem("Binance", "is required")], invalid.Problems);
    }

    [Fact]
    public void Every_wrong_key_is_reported_not_only_the_first()
    {
        var invalid = Assert.Throws<InvalidSettingsException>(() => Load(SettingsJson.Valid()
            .Without("Database:ConnectionString")
            .With("Email:Port", 0)
            .With("Security:MasterKeys:k2", "c2hvcnQ=")
            .With("Limits:MaxAgents", 3)));

        Assert.Equal(
            ["Database:ConnectionString", "Security:MasterKeys:k2", "Email:Port", "Limits:MaxAgents"],
            invalid.Problems.Select(problem => problem.Key));

        foreach (var problem in invalid.Problems)
        {
            Assert.Contains($"  {problem.Key}: {problem.Rule}", invalid.Message);
        }
    }

    [Fact]
    public void No_value_from_the_file_is_reported()
    {
        // Every text value becomes a marker, which makes addresses, levels, and keys invalid.
        using var directory = new SettingsDirectory(SettingsJson.Valid().WithEveryText($"{Marker} value"));

        var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

        Assert.True(invalid.Problems.Count > 10, "The markers were expected to break many keys.");
        Assert.DoesNotContain(Marker, invalid.Message);
        Assert.DoesNotContain(Marker, invalid.ToString());
    }

    [Fact]
    public void A_connection_string_that_cannot_be_read_is_reported_without_a_piece_of_it()
    {
        // A password with a semicolon in it, unquoted: what follows reads as a keyword of its own.
        var invalid = Assert.Throws<InvalidSettingsException>(() => Load(SettingsJson.Valid()
            .With("Database:ConnectionString", $"Host=db.example;Password=first;{Marker}=rest")));

        Assert.Equal(
            [new SettingsProblem("Database:ConnectionString", "must be a PostgreSQL connection string")],
            invalid.Problems);
        Assert.DoesNotContain(Marker, invalid.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secrets_do_not_print()
    {
        var settings = Load(SettingsJson.Valid());

        var printed = string.Join(
            Environment.NewLine, settings, settings.Database, settings.Security, settings.Email, settings.AiProviders);

        Assert.Contains("ConnectionString = ***", printed);
        Assert.DoesNotContain("database-password", printed);
        Assert.DoesNotContain("email-password", printed);
        Assert.DoesNotContain("claude-catalog-key", printed);
        Assert.DoesNotContain(SettingsJson.MasterKey, printed);
    }

    [Fact]
    public void Optional_keys_may_be_left_out_or_left_empty()
    {
        var settings = Load(SettingsJson.Valid()
            .Without("AiProviders:Claude:CatalogKey")
            .Without("Email:User")
            .With("Email:Password", "")
            .Without("Logging:Outputs:Console")
            .Without("Logging:Outputs:File"));

        Assert.Null(settings.AiProviders.ByCode[new AiProviderCode("claude")].CatalogKey);
        Assert.Null(settings.Email.User);
        Assert.Null(settings.Email.Password);
        Assert.False(settings.Logging.Outputs.Console);
        Assert.Null(settings.Logging.Outputs.File);
    }

    [Fact]
    public void An_empty_list_or_object_is_present_and_empty()
    {
        var settings = Load(SettingsJson.Valid()
            .WithJson("Application:Operators", "[]")
            .WithJson("Binance:OutboundIps", "[]")
            .WithJson("Logging:Overrides", "{}"));

        Assert.Empty(settings.Application.Operators);
        Assert.Empty(settings.Binance.OutboundIps);
        Assert.Empty(settings.Logging.Overrides);
    }

    [Fact]
    public void The_file_is_read_like_an_appsettings_file()
    {
        // Comments, trailing commas, and keys in any letter case, as in an appsettings file.
        var text = SettingsJson.Valid().ToString()
            .Replace("\"Limits\": {", "// How many agents one user may have.\n  \"limits\": {")
            .Replace("\"MaxAgentsPerUser\": 7", "\"MAXAGENTSPERUSER\": 7, /* fair use */");
        using var directory = new SettingsDirectory(text);

        var settings = SettingsFile.Load(directory.SettingsPath);

        Assert.Equal(7, settings.Limits.MaxAgentsPerUser);
    }

    [Fact]
    public void A_key_that_appears_twice_is_reported()
    {
        var text = SettingsJson.Valid().ToString()
            .Replace("\"MaxAgentsPerUser\": 7", "\"MaxAgentsPerUser\": 7, \"maxAgentsPerUser\": 8");
        using var directory = new SettingsDirectory(text);

        var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

        Assert.Equal([new SettingsProblem("Limits:maxAgentsPerUser", "appears more than once")], invalid.Problems);
    }

    [Fact]
    public void A_missing_file_is_reported_with_how_to_create_it()
    {
        using var directory = new SettingsDirectory();

        var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

        var problem = Assert.Single(invalid.Problems);
        Assert.Null(problem.Key);
        Assert.Contains("does not exist", problem.Rule);
        Assert.Contains($"Put {SettingsFile.Name} in this folder", problem.Rule);
        Assert.Contains(directory.SettingsPath, invalid.Message);
    }

    [Theory]
    [InlineData("{ \"Database\": { \"ConnectionString\": \"SECRET-MARKER\" \n \"Oops\": tru", "not valid JSON at line 2")]
    [InlineData("[ \"SECRET-MARKER\" ]", "must hold one JSON object")]
    public void A_file_that_is_not_a_JSON_object_is_reported_without_quoting_it(string text, string rule)
    {
        using var directory = new SettingsDirectory(text);

        var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

        var problem = Assert.Single(invalid.Problems);
        Assert.Null(problem.Key);
        Assert.Contains(rule, problem.Rule);
        Assert.DoesNotContain(Marker, invalid.Message);
    }

    [Fact]
    public void A_file_that_other_accounts_can_access_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no permission bits to check; see docs/configuration.md.");

        using var directory = new SettingsDirectory(SettingsJson.Valid());

        foreach (var open in new[] { UnixFileMode.GroupRead, UnixFileMode.OtherRead, UnixFileMode.OtherWrite })
        {
#pragma warning disable CA1416 // Skipped on Windows above.
            File.SetUnixFileMode(directory.SettingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | open);
#pragma warning restore CA1416

            var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

            var problem = Assert.Single(invalid.Problems);
            Assert.Null(problem.Key);
            Assert.Contains("chmod 600", problem.Rule);
        }
    }

    private static EcnanibSettings Load(SettingsJson json)
    {
        using var directory = new SettingsDirectory(json);

        return SettingsFile.Load(directory.SettingsPath);
    }
}
