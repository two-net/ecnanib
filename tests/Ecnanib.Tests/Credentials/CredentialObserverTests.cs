using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// The event Credentials raises for the modules that can't be called from it: Catalog now (FR-CAT-03) and
/// Agents later (FR-KEY-10, FR-KEY-11). See Inversions in docs/architecture.md.
/// </summary>
public sealed class CredentialObserverTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task Each_change_is_raised_once_after_it_commits()
    {
        var recorder = new Recorder();
        await using var app = new CredentialApp(postgres, services: services => services.AddSingleton<ICredentialObserver>(recorder));
        recorder.ConnectionString = app.ConnectionString;
        app.AiAccepts();
        app.BinanceAccepts();
        var userId = Guid.NewGuid();

        var added = (await app.AddAiAsync(userId)).Credential!;
        var binance = (await app.AddBinanceAsync(userId)).Credential!;
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => Ai.FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "no"));
        await app.AddAiAsync(userId, CredentialApp.OtherAiKey);
        app.AiAccepts();
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);
        await app.RotateAiAsync(userId, added.Id);
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => Ai.FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "down"));
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await app.Credentials.ValidateAsync(userId, added.Id, Cancellation);
        await app.Credentials.DeleteAsync(userId, added.Id, Cancellation);

        // The refused key and the check that couldn't reach the provider changed nothing, so they raised nothing.
        Assert.Equal(
            [
                new Seen(new CredentialChange(userId, added.Id, CredentialKind.Ai, CredentialApp.Provider, CredentialChangeKind.Added, CredentialStatus.Valid, null), "Valid"),
                new Seen(new CredentialChange(userId, binance.Id, CredentialKind.Exchange, CredentialProviders.Binance, CredentialChangeKind.Added, CredentialStatus.Valid, null), "Valid"),
                new Seen(new CredentialChange(userId, added.Id, CredentialKind.Ai, CredentialApp.Provider, CredentialChangeKind.Validated, CredentialStatus.Valid, CredentialStatus.Valid), "Valid"),
                new Seen(new CredentialChange(userId, added.Id, CredentialKind.Ai, CredentialApp.Provider, CredentialChangeKind.Rotated, CredentialStatus.Valid, CredentialStatus.Valid), "Valid"),
                new Seen(new CredentialChange(userId, added.Id, CredentialKind.Ai, CredentialApp.Provider, CredentialChangeKind.Deleted, null, CredentialStatus.Valid), null),
            ],
            recorder.Seen);
    }

    [Fact]
    public async Task An_observer_that_throws_neither_fails_the_change_nor_stops_the_others()
    {
        var recorder = new Recorder();
        await using var app = new CredentialApp(
            postgres,
            services: services =>
            {
                services.AddSingleton<ICredentialObserver, Throwing>();
                services.AddSingleton<ICredentialObserver>(recorder);
            });
        recorder.ConnectionString = app.ConnectionString;
        app.AiAccepts();

        var result = await app.AddAiAsync(Guid.NewGuid());

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(CredentialChangeKind.Added, Assert.Single(recorder.Seen).Change.Change);
    }

    /// <param name="Stored">The status another connection could read at that moment, or null when there was no row.</param>
    private sealed record Seen(CredentialChange Change, string? Stored);

    /// <summary>Records each change, with what another connection could read when it was raised, which proves it had committed.</summary>
    private sealed class Recorder : ICredentialObserver
    {
        private readonly List<Seen> _seen = [];

        public string ConnectionString { get; set; } = "";

        public IReadOnlyList<Seen> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public void OnChanged(CredentialChange change)
        {
            var stored = Sql.ColumnAsync(ConnectionString, $"SELECT status FROM credentials WHERE id = '{change.CredentialId}'", CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .SingleOrDefault();

            lock (_seen)
            {
                _seen.Add(new Seen(change, stored));
            }
        }
    }

    private sealed class Throwing : ICredentialObserver
    {
        public void OnChanged(CredentialChange change) => throw new InvalidOperationException("An observer broke.");
    }
}
