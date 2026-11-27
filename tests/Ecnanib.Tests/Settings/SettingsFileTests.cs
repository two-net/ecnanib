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
        var settings = Load(SettingsJson.Valid().WithCatalogKey());

        Assert.Equal("Ecnanib Test", settings.Application.Name);
        Assert.Equal(new Uri("https://ecnanib.example"), settings.Application.PublicUrl);
        Assert.False(settings.Application.RegistrationOpen);
        Assert.Equal(["operator@ecnanib.example"], settings.Application.Operators);

        Assert.Equal(
            "Host=db.example;Database=ecnanib;Username=app;Password=database-password",
            settings.ConnectionStrings.EcnanibDbContext.Reveal());

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
        Assert.Equal([new ModelPriceSettings("claude-priced-*", InputPerMillionTokens: 3m, OutputPerMillionTokens: 15.5m)], claude.Prices);
        var grok = settings.AiProviders.ByCode[new AiProviderCode("Grok")];
        Assert.Null(grok.CatalogKey);
        Assert.Equal(["grok-*"], grok.LiveSearch);
        Assert.Empty(grok.Prices);

        Assert.Equal(new CatalogSyncSettings(12, 3), settings.CatalogSync);

        Assert.Equal(new Uri("https://spot-live.example"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Live));
        Assert.Equal(new Uri("https://spot-demo.example"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Demo));
        Assert.Equal(new Uri("https://futures-live.example"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Live));
        Assert.Equal(new Uri("https://futures-demo.example"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Demo));

        // The file of the tests names no address of an account event feed: there is none then.
        Assert.All(
            new[] { (Market.Spot, TradingMode.Live), (Market.Spot, TradingMode.Demo), (Market.Futures, TradingMode.Live), (Market.Futures, TradingMode.Demo) },
            account => Assert.Null(settings.Binance.FeedUrl(account.Item1, account.Item2)));
        Assert.Equal(["BTCUSDT", "SOLUSDT"], settings.Binance.Symbols);
        Assert.Equal(["BNBUSDT"], settings.Binance.FeeSymbols);
        Assert.Equal([IPAddress.Parse("203.0.113.7")], settings.Binance.OutboundIps);
        Assert.Equal(60, settings.Binance.LimitSharePercent);
        Assert.Equal(4, settings.Binance.ReconcileMinutes);
        Assert.Equal(45, settings.Binance.ClockCheckSeconds);
        Assert.Equal(400, settings.Binance.ClockToleranceMilliseconds);

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
                MinRewardToRisk: new Floored<decimal>(1.25m, 1.75m),
                LeverageCap: new Bounded<int>(2, 4, 8),
                LiquidationBuffer: new Floored<decimal>(1.75m, 2.5m),
                DailyLossPercent: new Bounded<decimal>(1m, 5m, 12m),
                DrawdownPercent: new Bounded<decimal>(8m, 20m, 40m),
                MaxEntryDistancePercent: new Bounded<decimal>(0.2m, 0.8m, 2m)),
            settings.Trading.Aggressive);
        Assert.Equal(new StaleSecondsSettings(100, 200, 400, 800), settings.Trading.StaleSeconds);
        Assert.Equal(new CycleSettings(MaxPerProvider: 40, MaxPerKey: 3), settings.Trading.Cycles);
        Assert.Equal(new DataPackageSettings(Candles: 120, ContextCandles: 30), settings.Trading.DataPackage);
        Assert.Equal(25, settings.Trading.LiquidationWarningPercent);
        Assert.Equal(5, settings.Trading.RoleReassignmentDays);

        Assert.Equal(
            new ExperienceSettings(4, 15, 25, new ShadowFeeSettings(Spot: 0.2m, Futures: 0.04m), new ExperienceBoardSettings(6, 60)),
            settings.Experience);
        Assert.Equal(new RetentionSettings(30, 180, 45, 365), settings.Retention);
        Assert.Equal(
            new LimitsSettings(
                MaxAgentsPerUser: 7,
                SignIn: new AttemptLimitSettings(PerClient: 400, PerAddress: 200, Minutes: 20),
                Registration: new AttemptLimitSettings(PerClient: 300, PerAddress: 100, Minutes: 50),
                PasswordReset: new AttemptLimitSettings(PerClient: 350, PerAddress: 150, Minutes: 40)),
            settings.Limits);
    }

    /// <summary>Each row is a key to change, its new value as JSON, and the key that must be reported.</summary>
    public static TheoryData<string, string, string> WrongValues => new()
    {
        // Required keys.
        { "ConnectionStrings:EcnanibDbContext", "\"\"", "ConnectionStrings:EcnanibDbContext" },
        { "ConnectionStrings:EcnanibDbContext", "null", "ConnectionStrings:EcnanibDbContext" },
        { "Email", "null", "Email" },

        // Forms.
        { "ConnectionStrings:EcnanibDbContext", "\"db.example\"", "ConnectionStrings:EcnanibDbContext" },
        { "ConnectionStrings:EcnanibDbContext", "\"Host=db.example;Port=five\"", "ConnectionStrings:EcnanibDbContext" },
        { "ConnectionStrings:EcnanibDbContext", "\"Host=db.example;Passwrod=database-password\"", "ConnectionStrings:EcnanibDbContext" },

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

        // A hundredth of Binance's limit on requests does not hold the heaviest call that the
        // service makes, the read of funding on the Futures market, which would never pass.
        { "Binance:LimitSharePercent", "1", "Binance:LimitSharePercent" },
        { "Binance:ClockCheckSeconds", "29", "Binance:ClockCheckSeconds" },
        { "Binance:ClockCheckSeconds", "3601", "Binance:ClockCheckSeconds" },
        { "Binance:ClockToleranceMilliseconds", "0", "Binance:ClockToleranceMilliseconds" },
        { "Binance:ClockToleranceMilliseconds", "1000", "Binance:ClockToleranceMilliseconds" },
        { "CatalogSync:IntervalHours", "0", "CatalogSync:IntervalHours" },
        { "CatalogSync:IntervalHours", "8761", "CatalogSync:IntervalHours" },
        { "CatalogSync:MissedSyncsBeforeUnavailable", "0", "CatalogSync:MissedSyncsBeforeUnavailable" },
        { "CatalogSync:MissedSyncsBeforeUnavailable", "101", "CatalogSync:MissedSyncsBeforeUnavailable" },
        { "Trading:Aggressive:MinRewardToRisk", "[0, 1.5]", "Trading:Aggressive:MinRewardToRisk:0" },
        { "Trading:Aggressive:MinRewardToRisk", "[1, 100.5]", "Trading:Aggressive:MinRewardToRisk:1" },
        { "Trading:Aggressive:LiquidationBuffer", "[1, 2]", "Trading:Aggressive:LiquidationBuffer:0" },
        { "Trading:Aggressive:RiskPerTradePercent", "[0, 2, 5]", "Trading:Aggressive:RiskPerTradePercent:0" },
        { "Trading:Aggressive:DrawdownPercent", "[10, 25, 101]", "Trading:Aggressive:DrawdownPercent:2" },
        { "Trading:Aggressive:LeverageCap", "[1, 5, 126]", "Trading:Aggressive:LeverageCap:2" },
        { "Trading:Aggressive:RiskPerTradePercent", "[3, 2, 5]", "Trading:Aggressive:RiskPerTradePercent" },
        { "Trading:Aggressive:DailyLossPercent", "[2, 6]", "Trading:Aggressive:DailyLossPercent" },

        // A setting with a floor is two numbers, the minimum and the default, in that order.
        { "Trading:Aggressive:MinRewardToRisk", "1.5", "Trading:Aggressive:MinRewardToRisk" },
        { "Trading:Aggressive:MinRewardToRisk", "[1, 1.5, 3]", "Trading:Aggressive:MinRewardToRisk" },
        { "Trading:Aggressive:LiquidationBuffer", "[2.5, 2]", "Trading:Aggressive:LiquidationBuffer" },
        { "Experience:Board:MinUsers", "0", "Experience:Board:MinUsers" },

        // The fee of a shadow trade is a number of percent, and no negative one. It is one
        // number for each market, and not one for both.
        { "Experience:ShadowFeePercent:Spot", "-0.1", "Experience:ShadowFeePercent:Spot" },
        { "Experience:ShadowFeePercent:Futures", "1.01", "Experience:ShadowFeePercent:Futures" },
        { "Experience:ShadowFeePercent:Spot", "\"0.1\"", "Experience:ShadowFeePercent:Spot" },
        { "Experience:ShadowFeePercent", "0.1", "Experience:ShadowFeePercent" },
        { "Retention:AuditDays", "-1", "Retention:AuditDays" },

        // What is kept is kept for a day at the least, and for a hundred years at the most: a
        // number of days that reaches back before any calendar is no time that a job can count.
        { "Retention:TranscriptDays", "0", "Retention:TranscriptDays" },
        { "Retention:TranscriptDays", "36501", "Retention:TranscriptDays" },
        { "Retention:DecisionDays", "36501", "Retention:DecisionDays" },
        { "Retention:NotificationDays", "36501", "Retention:NotificationDays" },
        { "Retention:AuditDays", "2147483647", "Retention:AuditDays" },
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

        // Users reach the service over TLS and nothing else (NFR-SEC-1).
        { "Application:PublicUrl", "\"http://ecnanib.example\"", "Application:PublicUrl" },
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

        // A pattern is the identifier of a model, which is in one piece, and is listed once.
        { "AiProviders:Claude:Allow", "[\"claude-* gpt-*\"]", "AiProviders:Claude:Allow:0" },
        { "AiProviders:Claude:Deny", "[\"*-preview\", \"claude-\\t*\"]", "AiProviders:Claude:Deny:1" },
        { "AiProviders:Grok:LiveSearch", "[\"grok-*\", \"GROK-*\"]", "AiProviders:Grok:LiveSearch" },

        // What the tokens of a model cost (FR-CAT-11): a list, which may be empty, of a pattern
        // with two prices for a million tokens, each nothing or more. A pattern is listed once,
        // and an entry holds nothing else.
        { "AiProviders:Claude:Prices", "null", "AiProviders:Claude:Prices" },
        { "AiProviders:Claude:Prices", "{}", "AiProviders:Claude:Prices" },
        { "AiProviders:Claude:Prices", "[\"claude-*\"]", "AiProviders:Claude:Prices:0" },
        { "AiProviders:Claude:Prices", "[null]", "AiProviders:Claude:Prices:0" },
        {
            "AiProviders:Claude:Prices",
            "[{ \"Model\": \"claude-*\", \"InputPerMillionTokens\": 3 }]",
            "AiProviders:Claude:Prices:0:OutputPerMillionTokens"
        },
        { "AiProviders:Claude:Prices:0:Model", "\"\"", "AiProviders:Claude:Prices:0:Model" },
        { "AiProviders:Claude:Prices:0:Model", "\"claude-* gpt-*\"", "AiProviders:Claude:Prices:0:Model" },
        { "AiProviders:Claude:Prices:0:InputPerMillionTokens", "-0.01", "AiProviders:Claude:Prices:0:InputPerMillionTokens" },
        { "AiProviders:Claude:Prices:0:InputPerMillionTokens", "\"3\"", "AiProviders:Claude:Prices:0:InputPerMillionTokens" },
        { "AiProviders:Claude:Prices:0:OutputPerMillionTokens", "1000000.01", "AiProviders:Claude:Prices:0:OutputPerMillionTokens" },
        { "AiProviders:Claude:Prices:0:Currency", "\"EUR\"", "AiProviders:Claude:Prices:0:Currency" },
        {
            "AiProviders:Claude:Prices",
            "[{ \"Model\": \"claude-*\", \"InputPerMillionTokens\": 3, \"OutputPerMillionTokens\": 15 }, "
                + "{ \"Model\": \"CLAUDE-*\", \"InputPerMillionTokens\": 1, \"OutputPerMillionTokens\": 5 }]",
            "AiProviders:Claude:Prices"
        },
        { "Binance:Spot:Demo", "{}", "Binance:Spot:Demo:BaseUrl" },
        // The address of an account event feed is that of a WebSocket (FR-BIN-7).
        { "Binance:Spot:Demo:FeedUrl", "\"https://spot-demo.example\"", "Binance:Spot:Demo:FeedUrl" },
        { "Binance:Futures:Live:FeedUrl", "\"feed.example/private\"", "Binance:Futures:Live:FeedUrl" },

        // What is sent to a feed may hold a credential: it is reached over TLS, but for one on this machine.
        { "Binance:Spot:Live:FeedUrl", "\"ws://feed.example/ws-api/v3\"", "Binance:Spot:Live:FeedUrl" },
        { "Binance:Futures:Demo:FeedUrl", "7", "Binance:Futures:Demo:FeedUrl" },
        { "Binance:Symbols", "[]", "Binance:Symbols" },
        { "Binance:Symbols", "[\"btcusdt\"]", "Binance:Symbols:0" },

        // A symbol that prices a fee is written as one that is traded, and is listed once (FR-BIN-10).
        { "Binance:FeeSymbols", "\"BNBUSDT\"", "Binance:FeeSymbols" },
        { "Binance:FeeSymbols", "[\"BNB-USDT\"]", "Binance:FeeSymbols:0" },
        { "Binance:FeeSymbols", "[\"BNBUSDT\", \"BNBUSDT\"]", "Binance:FeeSymbols" },
        { "Binance:OutboundIps", "[\"300.1.1.1\"]", "Binance:OutboundIps:0" },
        { "Trading:StaleSeconds", "{ \"15m\": 100, \"1H\": 200, \"1D\": 800 }", "Trading:StaleSeconds:4H" },

        // How many cycles run at once (FR-CYC-11): one at least, for a provider and for a key.
        { "Trading:Cycles", "null", "Trading:Cycles" },
        { "Trading:Cycles", "{ \"MaxPerProvider\": 40 }", "Trading:Cycles:MaxPerKey" },
        { "Trading:Cycles:MaxPerProvider", "0", "Trading:Cycles:MaxPerProvider" },
        { "Trading:Cycles:MaxPerKey", "0", "Trading:Cycles:MaxPerKey" },
        { "Trading:Cycles:MaxPerKey", "\"3\"", "Trading:Cycles:MaxPerKey" },

        // How many candles the data package of a cycle holds (FR-CYC-2): one at least, and no
        // more than one read of the exchange gives.
        { "Trading:DataPackage", "null", "Trading:DataPackage" },
        { "Trading:DataPackage", "{ \"Candles\": 120 }", "Trading:DataPackage:ContextCandles" },
        { "Trading:DataPackage:Candles", "0", "Trading:DataPackage:Candles" },
        { "Trading:DataPackage:Candles", "499", "Trading:DataPackage:Candles" },
        { "Trading:DataPackage:ContextCandles", "0", "Trading:DataPackage:ContextCandles" },
        { "Trading:DataPackage:ContextCandles", "499", "Trading:DataPackage:ContextCandles" },
        { "Trading:DataPackage:ContextCandles", "\"30\"", "Trading:DataPackage:ContextCandles" },

        // How much of the way to its liquidation price a position of the Futures market has
        // left when its user is told (FR-BIN-F5): a share of the way, which is some of it and
        // not all of it.
        { "Trading:LiquidationWarningPercent", "null", "Trading:LiquidationWarningPercent" },
        { "Trading:LiquidationWarningPercent", "0", "Trading:LiquidationWarningPercent" },
        { "Trading:LiquidationWarningPercent", "100", "Trading:LiquidationWarningPercent" },
        { "Trading:LiquidationWarningPercent", "12.5", "Trading:LiquidationWarningPercent" },
        { "Trading:LiquidationWarningPercent", "\"25\"", "Trading:LiquidationWarningPercent" },

        // How many days pass at the least between two times that the roles of an agent are
        // reassigned by strength (FR-TEAM-10): whole days, one or more, and a year at the most.
        { "Trading:RoleReassignmentDays", "null", "Trading:RoleReassignmentDays" },
        { "Trading:RoleReassignmentDays", "0", "Trading:RoleReassignmentDays" },
        { "Trading:RoleReassignmentDays", "366", "Trading:RoleReassignmentDays" },
        { "Trading:RoleReassignmentDays", "1.5", "Trading:RoleReassignmentDays" },
        { "Trading:RoleReassignmentDays", "\"7\"", "Trading:RoleReassignmentDays" },

        // How often the account entry points may be tried (NFR-SEC-4).
        { "Limits:SignIn", "null", "Limits:SignIn" },
        { "Limits:SignIn:PerClient", "0", "Limits:SignIn:PerClient" },
        { "Limits:SignIn:PerAddress", "\"10\"", "Limits:SignIn:PerAddress" },
        { "Limits:Registration", "{ \"PerClient\": 10, \"Minutes\": 60 }", "Limits:Registration:PerAddress" },
        { "Limits:Registration:Minutes", "0", "Limits:Registration:Minutes" },
        { "Limits:PasswordReset:Minutes", "1441", "Limits:PasswordReset:Minutes" },
        { "Limits:PasswordReset:PerHour", "3", "Limits:PasswordReset:PerHour" },

        // Keys the application does not know. Runtime flags, user preferences, and agent settings
        // live in the database, so the file has no place for them (NFR-CFG-6).
        { "Limits:MaxAgentsPerUsers", "10", "Limits:MaxAgentsPerUsers" },
        { "Trading:StaleSeconds:1W", "3600", "Trading:StaleSeconds:1W" },
        { "Trading:Cycles:MaxPerUser", "10", "Trading:Cycles:MaxPerUser" },

        // The periods of the indicators are fixed: the file has no place for one.
        { "Trading:DataPackage:RsiPeriod", "14", "Trading:DataPackage:RsiPeriod" },

        // The product has one trading profile, Aggressive: the file has no place for another (FR-RSK-1).
        { "Trading:Conservative", "{ \"RiskPerTradePercent\": [0.1, 0.5, 1] }", "Trading:Conservative" },
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
            .Without("ConnectionStrings:EcnanibDbContext")
            .With("Email:Port", 0)
            .With("Security:MasterKeys:k2", "c2hvcnQ=")
            .With("Limits:MaxAgents", 3)));

        Assert.Equal(
            ["ConnectionStrings:EcnanibDbContext", "Security:MasterKeys:k2", "Email:Port", "Limits:MaxAgents"],
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
            .With("ConnectionStrings:EcnanibDbContext", $"Host=db.example;Password=first;{Marker}=rest")));

        Assert.Equal(
            [new SettingsProblem("ConnectionStrings:EcnanibDbContext", "must be a PostgreSQL connection string")],
            invalid.Problems);
        Assert.DoesNotContain(Marker, invalid.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secrets_do_not_print()
    {
        var settings = Load(SettingsJson.Valid().WithCatalogKey());

        var printed = string.Join(
            Environment.NewLine, settings, settings.ConnectionStrings, settings.Security, settings.Email, settings.AiProviders);

        Assert.Contains("EcnanibDbContext = ***", printed);
        Assert.DoesNotContain("database-password", printed);
        Assert.DoesNotContain("email-password", printed);
        Assert.DoesNotContain("claude-catalog-key", printed);
        Assert.DoesNotContain(SettingsJson.MasterKey, printed);
    }

    [Fact]
    public void The_address_of_an_account_event_feed_is_read_where_the_file_names_one()
    {
        // Each market has one in each environment, or none: where the file names none, or leaves
        // it empty, the account of a user is not followed there, and is reconciled on the
        // schedule alone (FR-BIN-7).
        var settings = Load(SettingsJson.Valid()
            .With("Binance:Spot:Demo:FeedUrl", "wss://spot-demo-feed.example:443/ws-api/v3")
            .With("Binance:Futures:Demo:FeedUrl", "ws://127.0.0.1:8123/private")
            .With("Binance:Futures:Live:FeedUrl", ""));

        Assert.Equal(new Uri("wss://spot-demo-feed.example:443/ws-api/v3"), settings.Binance.FeedUrl(Market.Spot, TradingMode.Demo));
        Assert.Equal(new Uri("ws://127.0.0.1:8123/private"), settings.Binance.FeedUrl(Market.Futures, TradingMode.Demo));
        Assert.Null(settings.Binance.FeedUrl(Market.Futures, TradingMode.Live));
        Assert.Null(settings.Binance.FeedUrl(Market.Spot, TradingMode.Live));
    }

    [Fact]
    public void Optional_keys_may_be_left_out_or_left_empty()
    {
        var settings = Load(SettingsJson.Valid()
            .WithCatalogKey()
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
            .WithJson("Binance:FeeSymbols", "[]")
            .WithJson("Logging:Overrides", "{}"));

        Assert.Empty(settings.Application.Operators);
        Assert.Empty(settings.Binance.OutboundIps);

        // No symbol prices a fee then: one in an asset of neither side of its symbol has no value.
        Assert.Empty(settings.Binance.FeeSymbols);
        Assert.Empty(settings.Logging.Overrides);
    }

    [Fact]
    public void The_file_is_read_like_an_appsettings_file()
    {
        // Comments, trailing commas, and keys in any letter case, as in an appsettings file.
        var text = SettingsJson.Valid().ToString()
            .Replace("\"Limits\": {", "// How many agents one user may have.\n  \"limits\": {")
            .Replace("\"MaxAgentsPerUser\": 7", "\"MAXAGENTSPERUSER\": 7 /* fair use */")
            .Replace("\"Minutes\": 40", "\"minutes\": 40, /* the last key, with a comma after it */");
        using var directory = new SettingsDirectory(text);

        var settings = SettingsFile.Load(directory.SettingsPath);

        Assert.Equal(7, settings.Limits.MaxAgentsPerUser);
        Assert.Equal(40, settings.Limits.PasswordReset.Minutes);
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
