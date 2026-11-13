using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Credentials;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// An agent that depends on a missing key moves to Attention required (FR-KEY-10). Credentials
/// may not use Agents, so it tells whatever depends on a key through
/// <see cref="IKeyDependents"/>, inside the transaction that deletes the key. Agents are not
/// built yet, so a stand-in of the tests is told for them, as Agents will be.
/// </summary>
public class KeyDependentsTests
{
    private const string Ada = "ada@example.com";

    [Fact]
    public async Task What_depends_on_a_key_is_told_in_the_transaction_that_deletes_it()
    {
        var dependents = new StandInDependents();
        using var application = new AccountsApplication(
            host: host => host.ConfigureServices(services => services.AddSingleton<IKeyDependents>(dependents)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();

        // Adding a key and replacing one leave a key where an agent looks for it, so nothing is told.
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreAiKeyAsync("claude", Keys.AnotherClaude);
        await client.StoreAiKeyAsync("grok", Keys.Of("ada", "grok"));
        await client.StoreExchangeKeyAsync("live", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("live", Keys.Of("ada", "live"), Keys.Of("ada-sealed", "live"));

        Assert.Empty(dependents.Told);

        // Nor is deleting where nothing is stored: no key went.
        await client.DeleteAiKeyAsync("gemini");
        await client.DeleteExchangeKeyAsync("demo");

        Assert.Empty(dependents.Told);

        await client.StoreExchangeKeyAsync("demo", Keys.Of("ada", "demo"), Keys.Of("ada-sealed", "demo"));

        // Deleting a key tells it, with whose key it is and which. It is told with the context
        // that holds the transaction, while the key is still stored: what it changes is stored
        // when the key goes, and not when it does not.
        await client.DeleteAiKeyAsync("claude");
        await client.DeleteExchangeKeyAsync("live");

        Assert.Equal(
            [
                new Told(user.Id, "claude", InTransaction: true, KeyStillStored: true),
                new Told(user.Id, "Live", InTransaction: true, KeyStillStored: true),
            ],
            dependents.Told);

        // The key is gone, and what the stand-in changed when it was told is stored with that:
        // the mark it left on the other key of the user.
        Assert.Equal(["grok"], StoredAiCredential.All(application.Database).Select(key => key.Provider));
        Assert.Equal(StandInDependents.Mark, StoredAiCredential.Of(application.Database, user.Id, "grok").MaskedSuffix);
        Assert.Equal(["Demo"], StoredExchangeCredential.All(application.Database).Select(key => key.Environment));
        Assert.Equal(StandInDependents.Mark, StoredExchangeCredential.Of(application.Database, user.Id, "Demo").MaskedSuffix);
    }

    [Fact]
    public async Task A_key_is_not_deleted_when_what_depends_on_it_could_not_be_told()
    {
        // Two that depend on a key: the first changes what it keeps, and the second fails. As a
        // deployment runs, where an error is answered with the error page.
        var first = new StandInDependents();
        var dependents = new StandInDependents { Fails = true };
        using var application = new AccountsApplication(
            host: host => host
                .UseEnvironment("Production")
                .ConfigureServices(services => services
                    .AddSingleton<IKeyDependents>(first)
                    .AddSingleton<IKeyDependents>(dependents)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.ConfirmIdentityAsync();
        await client.StoreAiKeyAsync("claude", Keys.Claude);
        await client.StoreAiKeyAsync("grok", Keys.Of("ada", "grok"));
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);
        await client.StoreExchangeKeyAsync("live", Keys.Of("ada", "live"), Keys.Of("ada-sealed", "live"));
        var stored = application.RowsOf(user.Id);

        // An agent must not be left running on a key that is gone. So when the change to the
        // agents fails, the key stays: an error, and nothing of the deletion is stored.
        foreach (var page in new[] { KeyBrowser.DeleteAiKeyPage("claude"), KeyBrowser.DeleteExchangeKeyPage("demo") })
        {
            using var answer = await client.SendFormAsync(
                page, KeyBrowser.DeleteForm, Browser.AntiforgeryTokenIn(await client.PageAsync(page)));

            Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        }

        // Both were told, both times. What the first one changed is undone with the rest: it was
        // told inside the transaction that deletes the key, and not in one of its own. So every
        // row of the user is as it was, the mark of the first one included.
        Assert.Equal(2, first.Told.Count);
        Assert.Equal(2, dependents.Told.Count);
        Assert.Equal(TextOf(stored), TextOf(application.RowsOf(user.Id)));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "KeyDeleted");

        // Once it can be told, the key is deleted.
        dependents.Fails = false;
        Assert.Equal(KeyBrowser.KeysPage, (await client.DeleteAiKeyAsync("claude")).Path);
        Assert.Equal(["grok"], StoredAiCredential.All(application.Database).Select(key => key.Provider));

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    [Fact]
    public void Once_agents_are_stored_the_component_that_keeps_them_is_told_when_a_key_goes()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Agents are not built yet, and nothing is told for them. As soon as the component has
        // a table of its own, it must be: Credentials cannot look at agents itself.
        var storesRows = model.Model.GetEntityTypes().Any(entity => IsOfAgents(entity.ClrType));
        var isTold = application.Services.GetServices<IKeyDependents>().Any(dependent => IsOfAgents(dependent.GetType()));

        Assert.True(
            isTold || !storesRows,
            "Agents stores rows, and nothing of it is told when a key that an agent depends on is deleted. An agent that " +
            $"depends on a missing key moves to Attention required (FR-KEY-10). Implement {nameof(IKeyDependents)} in " +
            "Ecnanib.Agents and register it in AddAgents(). See docs/credentials.md.");

        static bool IsOfAgents(Type type) =>
            type.Namespace is { } area && (area == "Ecnanib.Agents" || area.StartsWith("Ecnanib.Agents.", StringComparison.Ordinal));
    }

    /// <summary>One time that <see cref="StandInDependents"/> was told, with how.</summary>
    /// <param name="UserId">The user whose key goes.</param>
    /// <param name="Key">Which key: the code of the provider, or the name of the environment.</param>
    /// <param name="InTransaction">Whether the context it was told with holds a transaction.</param>
    /// <param name="KeyStillStored">Whether that context still finds the key.</param>
    private sealed record Told(Guid UserId, string Key, bool InTransaction, bool KeyStillStored);

    /// <summary>
    /// Stands for Agents, which is not built yet: it is told when a key goes, keeps how it was
    /// told, and fails when a test tells it to. As Agents will change the state of an agent, it
    /// changes something with the context it is given: it leaves a mark on the other keys of
    /// the user, where a test can see whether the change was stored.
    /// </summary>
    private sealed class StandInDependents : IKeyDependents
    {
        /// <summary>What it writes in place of the last four characters of the other keys of the user.</summary>
        public const string Mark = "told";

        private readonly ConcurrentQueue<Told> _told = new();

        /// <summary>Whether it fails, as a change to the agents that cannot be stored would.</summary>
        public bool Fails { get; set; }

        /// <summary>The times it was told, in order.</summary>
        public IReadOnlyList<Told> Told => [.. _told];

        public async Task AiKeyDeletedAsync(
            EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
        {
            var stored = await context.Set<AiCredential>()
                .OwnedBy(userId)
                .AnyAsync(credential => credential.Provider == provider, cancellationToken);

            await context.Set<AiCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Provider != provider)
                .ExecuteUpdateAsync(row => row.SetProperty(credential => credential.MaskedSuffix, Mark), cancellationToken);

            Tell(new Told(userId, provider.Value, context.Database.CurrentTransaction is not null, stored));
        }

        public async Task ExchangeKeyDeletedAsync(
            EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken)
        {
            var stored = await context.Set<ExchangeCredential>()
                .OwnedBy(userId)
                .AnyAsync(credential => credential.Environment == environment, cancellationToken);

            await context.Set<ExchangeCredential>()
                .OwnedBy(userId)
                .Where(credential => credential.Environment != environment)
                .ExecuteUpdateAsync(row => row.SetProperty(credential => credential.MaskedSuffix, Mark), cancellationToken);

            Tell(new Told(userId, environment.ToString(), context.Database.CurrentTransaction is not null, stored));
        }

        private void Tell(Told told)
        {
            _told.Enqueue(told);

            if (Fails)
            {
                throw new InvalidOperationException("The agents of the test could not be changed.");
            }
        }
    }
}
