using System.Net.Http;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-ROLE-02 to FR-ROLE-05: the models a user can give each role, and the assignment a new agent starts with,
/// from the RoleDefaults section and the models the user's keys can use.
/// </summary>
public sealed class RoleAssignmentTests(PostgresFixture postgres)
{
    private const string Test = AgentApp.Provider;
    private const string Second = AgentApp.Second;

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task Only_models_that_can_give_structured_answers_are_offered_and_an_unknown_one_counts()
    {
        await using var app = new AgentApp(postgres, Overrides(("model-b", "StructuredOutput", "false"), ("model-c", "StructuredOutput", "true")));
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Models(null, "model-a", "model-b", "model-c"));
        var user = await app.UserAsync();

        var choices = await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation);

        Assert.All(Enum.GetValues<AiRole>(), role => Assert.Equal(["model-a", "model-c"], choices.Options[role].Select(choice => choice.Model.ModelId)));
        Assert.DoesNotContain(choices.Defaults, assignment => assignment.Model.ModelId == "model-b" || assignment.Fallback?.ModelId == "model-b");
    }

    [Fact]
    public async Task The_Sentiment_Analyst_prefers_models_with_live_search()
    {
        await using var app = new AgentApp(postgres, Overrides(("model-b", "LiveSearch", "true"), ("model-c", "LiveSearch", "false")));
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Models(null, "model-a", "model-b", "model-c"));
        var user = await app.UserAsync();

        var choices = await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation);

        Assert.Equal(["model-b", "model-a", "model-c"], choices.Options[AiRole.SentimentAnalyst].Select(choice => choice.Model.ModelId));
        Assert.Equal(["model-a", "model-b", "model-c"], choices.Options[AiRole.Strategist].Select(choice => choice.Model.ModelId));
        Assert.Equal([null, true, false], choices.Options[AiRole.Strategist].Select(choice => choice.LiveSearch));
        Assert.Equal(new ModelRef(Test, "model-b"), Default(choices, AiRole.SentimentAnalyst).Model);
        Assert.Equal(new ModelRef(Test, "model-a"), Default(choices, AiRole.Strategist).Model);
    }

    /// <summary>A role takes its default model, else its default provider's newest, by release date and then by ID.</summary>
    [Fact]
    public async Task A_role_takes_its_default_model_or_else_its_providers_newest()
    {
        await using var app = new AgentApp(postgres, new()
        {
            ["RoleDefaults:Strategist:Provider"] = Test,
            ["RoleDefaults:Strategist:Model"] = "model-b",
            ["RoleDefaults:TechnicalAnalyst"] = Test,
            ["RoleDefaults:RiskCritic:Provider"] = Test,
            ["RoleDefaults:RiskCritic:Model"] = "model-gone",
        });
        app.Ai.On(
            HttpMethod.Get,
            CredentialApp.ModelsPath,
            _ => Listing(("model-a", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), ("model-b", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)), ("model-c", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)), ("model-d", null)));
        var user = await app.UserAsync();

        var choices = await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation);

        Assert.Equal(
            [
                new RoleAssignment(AiRole.TechnicalAnalyst, new(Test, "model-c"), new(Test, "model-a")),
                new RoleAssignment(AiRole.SentimentAnalyst, new(Test, "model-c"), new(Test, "model-a")),
                new RoleAssignment(AiRole.Strategist, new(Test, "model-b"), new(Test, "model-c")),
                new RoleAssignment(AiRole.RiskCritic, new(Test, "model-c"), new(Test, "model-a")),
                new RoleAssignment(AiRole.Reviewer, new(Test, "model-c"), new(Test, "model-a")),
            ],
            choices.Defaults);
    }

    /// <summary>FR-ROLE-05: a user with one AI key gets every role from it, whatever the defaults name.</summary>
    [Fact]
    public async Task A_single_key_fills_every_role_and_the_agent_activates()
    {
        await using var app = new AgentApp(postgres, new()
        {
            ["RoleDefaults:TechnicalAnalyst"] = Second,
            ["RoleDefaults:Strategist"] = Second,
            ["RoleDefaults:RiskCritic"] = Second,
        });
        var user = await app.UserAsync();
        var model = new ModelRef(Test, AgentApp.Model);

        var choices = await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation);

        Assert.Equal(Enum.GetValues<AiRole>().Select(role => new RoleAssignment(role, model)), choices.Defaults);

        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user) with { Roles = choices.Defaults });
        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Equal([new ActivationWarning(ActivationWarningKind.RiskCriticSharesProvider, AiRole.RiskCritic)], result.Warnings);
    }

    /// <summary>FR-ROLE-04, and a fallback from another provider, so one provider's outage doesn't take both.</summary>
    [Fact]
    public async Task The_Risk_Critic_and_each_fallback_take_another_provider_where_there_is_one()
    {
        await using var app = new AgentApp(postgres, new()
        {
            ["RoleDefaults:Strategist"] = Test,
            ["RoleDefaults:RiskCritic"] = Test,
        });
        app.Ai.On(
            HttpMethod.Get,
            CredentialApp.ModelsPath,
            request => FakeAiProvider.Models(null, request.Key == CredentialApp.OtherAiKey ? "model-x" : AgentApp.Model));
        var user = await app.UserAsync();
        await app.AddAiAsync(user.Id, Second, CredentialApp.OtherAiKey);
        var test = new ModelRef(Test, AgentApp.Model);
        var second = new ModelRef(Second, "model-x");

        var choices = await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation);

        // A role with no default takes the providers by name.
        Assert.Equal(
            [
                new RoleAssignment(AiRole.TechnicalAnalyst, second, test),
                new RoleAssignment(AiRole.SentimentAnalyst, second, test),
                new RoleAssignment(AiRole.Strategist, test, second),
                new RoleAssignment(AiRole.RiskCritic, second, test),
                new RoleAssignment(AiRole.Reviewer, second, test),
            ],
            choices.Defaults);

        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user) with { Roles = choices.Defaults });
        var result = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Empty(result.Warnings!);
    }

    [Fact]
    public async Task Without_a_usable_model_nothing_is_offered_or_assigned()
    {
        await using var app = new AgentApp(postgres, Overrides(("model-a", "StructuredOutput", "false")));
        var withoutKeys = Guid.NewGuid();
        var user = await app.UserAsync();

        foreach (var userId in new[] { withoutKeys, user.Id })
        {
            var choices = await app.Agents.ListRoleChoicesAsync(userId, Cancellation);

            Assert.All(Enum.GetValues<AiRole>(), role => Assert.Empty(choices.Options[role]));
            Assert.Empty(choices.Defaults);
        }
    }

    /// <summary>NFR-SEC-04: only the user's own valid keys count.</summary>
    [Fact]
    public async Task Another_users_keys_and_an_invalid_key_offer_nothing()
    {
        await using var app = new AgentApp(postgres);
        app.Ai.On(
            HttpMethod.Get,
            CredentialApp.ModelsPath,
            request => FakeAiProvider.Models(null, request.Key == CredentialApp.OtherAiKey ? "model-x" : AgentApp.Model));
        var owner = await app.UserAsync();
        var stranger = Guid.NewGuid();
        await app.AddAiAsync(stranger, Second, CredentialApp.OtherAiKey);

        var choices = await app.Agents.ListRoleChoicesAsync(owner.Id, Cancellation);

        Assert.Equal([new ModelRef(Test, AgentApp.Model)], choices.Options[AiRole.Strategist].Select(choice => choice.Model));

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE credentials SET status = 'Invalid' WHERE id = '{owner.AiKey}'", Cancellation);

        Assert.Empty((await app.Agents.ListRoleChoicesAsync(owner.Id, Cancellation)).Defaults);
    }

    private static RoleAssignment Default(RoleChoices choices, AiRole role) => choices.Defaults.Single(assignment => assignment.Role == role);

    /// <summary>Catalog:Overrides for the Test provider's models, one field each.</summary>
    private static Dictionary<string, string?> Overrides(params (string Model, string Field, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>();

        foreach (var (index, (model, field, value)) in overrides.Index())
        {
            settings[$"Catalog:Overrides:{index}:Provider"] = Test;
            settings[$"Catalog:Overrides:{index}:Model"] = model;
            settings[$"Catalog:Overrides:{index}:{field}"] = value;
        }

        return settings;
    }

    /// <summary>A model list with release dates, which the Test provider gives as Unix seconds.</summary>
    private static HttpResponseMessage Listing(params (string Id, DateTimeOffset? Created)[] models) =>
        FakeAiProvider.Json(JsonSerializer.Serialize(new
        {
            models = models.Select(model => new { id = model.Id, name = $"Model {model.Id}", created = model.Created?.ToUnixTimeSeconds() }),
        }));
}
