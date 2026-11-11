using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// A configuration file as JSON that a test can change one key at a time. Keys are paths with
/// <c>:</c> between levels, as the application reports them.
/// </summary>
internal sealed class SettingsJson
{
    /// <summary>A master key of the right length: 32 bytes in Base64.</summary>
    public static readonly string MasterKey = Convert.ToBase64String([.. Enumerable.Range(1, 32).Select(i => (byte)i)]);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly JsonObject _root;

    private SettingsJson(JsonObject root) => _root = root;

    /// <summary>
    /// A complete file that passes validation. Its values differ from the defaults in the tracked
    /// file, so a test that reads one back knows it came from this file.
    /// </summary>
    public static SettingsJson Valid() => Parse(
        """
        {
          "Application": {
            "Name": "Ecnanib Test",
            "PublicUrl": "https://ecnanib.example",
            "RegistrationOpen": false,
            "Operators": [ "operator@ecnanib.example" ]
          },
          "ConnectionStrings": { "EcnanibDbContext": "Host=db.example;Database=ecnanib;Username=app;Password=database-password" },
          "Security": {
            "ActiveKeyId": "k2",
            "MasterKeys": { "k1": "", "k2": "" },
            "SessionIdleMinutes": 45
          },
          "Logging": {
            "MinimumLevel": "Warning",
            "Overrides": { "Microsoft.AspNetCore": "Error" },
            "Outputs": {
              "Console": {},
              "File": { "Path": "logs/test.log", "Rolling": "Day", "RetainedFiles": 7 }
            }
          },
          "Email": { "Host": "mail.example", "Port": 2525, "User": "mailer", "Password": "email-password", "From": "noreply@ecnanib.example" },
          "AiProviders": {
            "Claude": { "BaseUrl": "https://claude.example", "CatalogKey": "claude-catalog-key", "TimeoutSeconds": 30, "Allow": [ "claude-*" ], "Deny": [ "*-preview" ], "LiveSearch": [] },
            "Gemini": { "BaseUrl": "https://gemini.example", "CatalogKey": "", "TimeoutSeconds": 31, "Allow": [], "Deny": [], "LiveSearch": [] },
            "GPT": { "BaseUrl": "https://gpt.example", "CatalogKey": "", "TimeoutSeconds": 32, "Allow": [], "Deny": [], "LiveSearch": [] },
            "Grok": { "BaseUrl": "https://grok.example", "CatalogKey": "", "TimeoutSeconds": 33, "Allow": [], "Deny": [], "LiveSearch": [ "grok-*" ] }
          },
          "CatalogSync": { "IntervalHours": 12, "MissedSyncsBeforeUnavailable": 3 },
          "Binance": {
            "Spot": {
              "Live": { "BaseUrl": "https://spot-live.example" },
              "Demo": { "BaseUrl": "https://spot-demo.example" }
            },
            "Futures": {
              "Live": { "BaseUrl": "https://futures-live.example" },
              "Demo": { "BaseUrl": "https://futures-demo.example" }
            },
            "Symbols": [ "BTCUSDT", "SOLUSDT" ],
            "OutboundIps": [ "203.0.113.7" ],
            "LimitSharePercent": 60,
            "ReconcileMinutes": 4
          },
          "Trading": {
            "DefaultRoles": { "MarketAnalyst": "Claude", "SentimentAnalyst": "Grok", "Strategist": "GPT", "RiskManager": "Gemini" },
            "Aggressive": {
              "RiskPerTradePercent": [ 0.25, 1.5, 4 ],
              "MinRewardToRisk": 1.75,
              "LeverageCap": [ 2, 4, 8 ],
              "LiquidationBuffer": 2.5,
              "DailyLossPercent": [ 1, 5, 12 ],
              "DrawdownPercent": [ 8, 20, 40 ],
              "MaxEntryDistancePercent": [ 0.2, 0.8, 2 ]
            },
            "StaleSeconds": { "15m": 100, "1H": 200, "4H": 400, "1D": 800 }
          },
          "Experience": {
            "LessonsPerPrompt": 4,
            "MinSamples": 15,
            "Damping": 25,
            "Board": { "MinUsers": 6, "MinTrades": 60 }
          },
          "Retention": { "TranscriptDays": 30, "DecisionDays": 180, "NotificationDays": 45, "AuditDays": 365 },
          "Limits": { "MaxAgentsPerUser": 7 }
        }
        """)
        .With("Security:MasterKeys:k1", Convert.ToBase64String(new byte[32]))
        .With("Security:MasterKeys:k2", MasterKey);

    public static SettingsJson Parse(string json) => new(JsonNode.Parse(json)!.AsObject());

    /// <summary>
    /// Points the file at the shared database of the test server, for a test that starts the
    /// application: starting it migrates the database, so the database must be there.
    /// </summary>
    public SettingsJson OnTestServer() => OnDatabase(PostgresServer.SharedDatabase);

    /// <summary>Points the file at a database of the test's own.</summary>
    public SettingsJson OnDatabase(TestDatabase database) => With("ConnectionStrings:EcnanibDbContext", database.ConnectionString);

    /// <summary>Sets a key, adding it when it is not there.</summary>
    public SettingsJson With(string key, JsonNode? value)
    {
        var (parent, name) = Locate(key);
        if (parent is JsonArray list)
        {
            list[int.Parse(name)] = value;
        }
        else
        {
            parent[name] = value;
        }

        return this;
    }

    /// <summary>Sets a key to a value written as JSON, such as <c>[1, 2]</c> or <c>"text"</c>.</summary>
    public SettingsJson WithJson(string key, string json) => With(key, JsonNode.Parse(json));

    public SettingsJson Without(string key)
    {
        var (parent, name) = Locate(key);
        parent.AsObject().Remove(name);

        return this;
    }

    /// <summary>Replaces every text value in the file, at any depth.</summary>
    public SettingsJson WithEveryText(string text)
    {
        Replace(_root);
        return this;

        void Replace(JsonNode node)
        {
            switch (node)
            {
                case JsonObject children:
                    foreach (var name in children.Select(child => child.Key).ToList())
                    {
                        ReplaceOrDescend(children[name], replacement => children[name] = replacement);
                    }

                    break;

                case JsonArray items:
                    for (var index = 0; index < items.Count; index++)
                    {
                        var position = index;
                        ReplaceOrDescend(items[position], replacement => items[position] = replacement);
                    }

                    break;
            }
        }

        void ReplaceOrDescend(JsonNode? child, Action<JsonNode> replace)
        {
            if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                replace(JsonValue.Create(text));
            }
            else if (child is not null)
            {
                Replace(child);
            }
        }
    }

    public override string ToString() => _root.ToJsonString(Indented);

    private (JsonNode Parent, string Name) Locate(string key)
    {
        var levels = key.Split(':');

        JsonNode node = _root;
        foreach (var level in levels[..^1])
        {
            node = (node is JsonArray list ? list[int.Parse(level)] : node[level])
                ?? throw new ArgumentException($"'{key}' has no '{level}' to go into.", nameof(key));
        }

        return (node, levels[^1]);
    }
}
