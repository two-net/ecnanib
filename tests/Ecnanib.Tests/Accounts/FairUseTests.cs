using Ecnanib.Accounts;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// A user whose email address is not verified cannot create agents (FR-ACC-1). Fair-use limits
/// are read from the configuration file and apply equally to all users (FR-ACC-7).
/// </summary>
public class FairUseTests
{
    [Fact]
    public async Task A_user_whose_address_is_not_verified_cannot_create_agents()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync("ada@example.com");
        var user = StoredUser.Of(application.Database, "ada@example.com")!;

        Assert.Equal(AgentCreation.EmailNotVerified, await MayCreateAgentAsync(application, user.Id, agentsOwned: 0));

        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[0])));

        Assert.Equal(AgentCreation.Allowed, await MayCreateAgentAsync(application, user.Id, agentsOwned: 0));
    }

    [Fact]
    public async Task A_user_that_does_not_exist_cannot_create_agents()
    {
        using var application = new AccountsApplication();

        Assert.Equal(AgentCreation.EmailNotVerified, await MayCreateAgentAsync(application, Guid.NewGuid(), agentsOwned: 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task The_agent_limit_is_the_one_in_the_configuration_file_for_every_user(int limit)
    {
        using var application = new AccountsApplication(settings: file => file.With("Limits:MaxAgentsPerUser", limit));
        var ada = await application.RegisterVerifiedAsync("ada@example.com");
        var grace = await application.RegisterVerifiedAsync("grace@example.com");

        Assert.Equal(limit, application.FairUse.MaxAgentsPerUser);

        foreach (var user in new[] { ada, grace })
        {
            Assert.Equal(AgentCreation.Allowed, await MayCreateAgentAsync(application, user.Id, agentsOwned: limit - 1));
            Assert.Equal(AgentCreation.AgentLimitReached, await MayCreateAgentAsync(application, user.Id, agentsOwned: limit));
            Assert.Equal(AgentCreation.AgentLimitReached, await MayCreateAgentAsync(application, user.Id, agentsOwned: limit + 1));
        }
    }

    [Fact]
    public async Task An_unverified_user_is_told_to_verify_whatever_the_limit()
    {
        using var application = new AccountsApplication();
        await application.RegisterAsync("ada@example.com");
        var user = StoredUser.Of(application.Database, "ada@example.com")!;

        Assert.Equal(AgentCreation.EmailNotVerified, await MayCreateAgentAsync(application, user.Id, agentsOwned: 100));
    }

    private static Task<AgentCreation> MayCreateAgentAsync(AccountsApplication application, Guid userId, int agentsOwned) =>
        application.FairUse.MayCreateAgentAsync(userId, agentsOwned, TestContext.Current.CancellationToken);
}
