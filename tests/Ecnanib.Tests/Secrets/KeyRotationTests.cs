using System.Text;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Jobs;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Secrets;

/// <summary>
/// FR-KEY-08: when the configuration marks a new key active, every secret is re-encrypted in the
/// background, and the older keys keep decrypting until that has finished. Each test restarts the
/// application on one database, as an operator would.
/// </summary>
public sealed class KeyRotationTests(PostgresFixture postgres)
{
    private const string OldKeyId = AppFactory.TestKeyId;
    private const string NewKeyId = "2026-12";
    private const string Purpose = "Ecnanib.Tests.KeyRotation";

    private static readonly string NewKey = TestKeys.NewKey();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Both keys, with the new one active.</summary>
    private static Dictionary<string, string?> Rotated =>
        TestKeys.Settings((OldKeyId, AppFactory.TestKey, false), (NewKeyId, NewKey, true));

    /// <summary>Only the new key, as once the old one has been removed.</summary>
    private static Dictionary<string, string?> OnlyNew => TestKeys.Settings((NewKeyId, NewKey, true));

    [Fact]
    public async Task Older_keys_still_decrypt_until_reencryption_has_run()
    {
        var database = postgres.NewConnectionString();
        var (first, second) = (CanaryCases.NewCanary(), CanaryCases.NewCanary());
        Guid firstId, secondId;

        await using (var before = new AppFactory(database))
        {
            firstId = await SecretApp.AddAsync(before, first);
            secondId = await SecretApp.AddAsync(before, second);
        }

        await using var app = new AppFactory(database, HoldReencryption, Rotated);

        Assert.Equal(first, await SecretApp.RevealAsync(app, firstId));
        Assert.Equal(second, await SecretApp.RevealAsync(app, secondId));
        Assert.Equal(OldKeyId, await SecretApp.KeyIdAsync(database, firstId));

        var added = await SecretApp.AddAsync(app, CanaryCases.NewCanary());
        Assert.Equal(NewKeyId, await SecretApp.KeyIdAsync(database, added));

        var progress = await SecretApp.ProgressAsync(app);
        Assert.Equal(NewKeyId, progress.ActiveKeyId);
        Assert.NotNull(progress.ActiveSince);
        Assert.True(progress.Pending >= 2, $"{progress.Pending} pending");
        Assert.False(progress.Complete);
    }

    [Fact]
    public async Task Rotation_reencrypts_every_secret_and_then_the_old_key_can_go()
    {
        var database = postgres.NewConnectionString();
        var value = CanaryCases.NewCanary();
        Guid id;
        byte[] payload;

        await using (var before = new AppFactory(database))
        {
            id = await SecretApp.AddAsync(before, value);
            payload = Protector(before).Protect(Encoding.UTF8.GetBytes(value));
        }

        await using (var rotating = new AppFactory(database, settings: Rotated))
        {
            rotating.Start();
            await LoggedApp.UntilAsync(async () => (await SecretApp.ProgressAsync(rotating)).Complete);
        }

        Assert.Empty(await Sql.ColumnAsync(database, $"SELECT key_id FROM secrets WHERE key_id <> '{NewKeyId}'", Cancellation));

        await using var after = new AppFactory(database, settings: OnlyNew);

        Assert.Equal(value, await SecretApp.RevealAsync(after, id));
        Assert.Equal(value, Encoding.UTF8.GetString(Protector(after).Unprotect(payload)));
        Assert.True((await SecretApp.ProgressAsync(after)).Complete);
    }

    [Fact]
    public async Task Removing_a_key_that_secrets_still_use_stops_startup_and_names_it()
    {
        var database = postgres.NewConnectionString();

        await using (var before = new AppFactory(database))
        {
            await SecretApp.AddAsync(before, CanaryCases.NewCanary());
        }

        await using var app = new AppFactory(database, settings: OnlyNew);

        var exception = Assert.ThrowsAny<Exception>(app.Start);
        Assert.Contains(
            $"""Security:EncryptionKeys has no key with Id "{OldKeyId}", which""",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>FR-AUD-01. Restarting with the same keys isn't another rotation, and the first key isn't one at all.</summary>
    [Fact]
    public async Task A_rotation_is_audited_once()
    {
        var database = postgres.NewConnectionString();

        await using (var first = new AppFactory(database))
        {
            first.Start();
        }

        Assert.Equal(0, await RotationRecordsAsync(database));

        for (var start = 0; start < 2; start++)
        {
            await using var app = new AppFactory(database, HoldReencryption, Rotated);
            app.Start();
        }

        Assert.Equal(1, await RotationRecordsAsync(database));

        var record = Assert.Single(await Sql.ColumnAsync(
            database,
            "SELECT concat_ws(' ', actor_kind, user_id, target_type, target_id, summary::text) FROM audit_events WHERE action = 'MasterKeyRotated'",
            Cancellation));

        Assert.StartsWith($"System MasterKey {NewKeyId} ", record, StringComparison.Ordinal);
        Assert.Contains($"\"before\": \"{OldKeyId}\"", record, StringComparison.Ordinal);
        Assert.Contains($"\"after\": \"{NewKeyId}\"", record, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret that won't decrypt stays on its key, which startup then won't let anyone remove, and
    /// one bad row doesn't hold up the rest.
    /// </summary>
    [Fact]
    public async Task A_secret_that_cant_be_decrypted_is_skipped_and_reported()
    {
        var database = postgres.NewConnectionString();
        Guid good, bad;

        await using (var before = new AppFactory(database))
        {
            good = await SecretApp.AddAsync(before, CanaryCases.NewCanary());
            bad = await SecretApp.AddAsync(before, CanaryCases.NewCanary());
        }

        await SecretApp.CorruptAsync(database, bad);

        await using var app = new LoggedApp(database, Rotated);
        await JobRows.UntilStatusAsync(database, "secret-reencryption-on-start", JobStatus.Succeeded);

        Assert.Equal(NewKeyId, await SecretApp.KeyIdAsync(database, good));
        Assert.Equal(OldKeyId, await SecretApp.KeyIdAsync(database, bad));
        Assert.Matches(@"^Succeeded \d+ re-encrypted, 1 unreadable, 1 remaining ", await JobRows.TextAsync(database, "secret-reencryption-on-start"));

        await app.StopAsync();
        var log = await app.TableTextAsync(Cancellation);

        Assert.Contains($"Secret {bad} couldn't be decrypted with key \"{OldKeyId}\"", log, StringComparison.Ordinal);
        Assert.Contains("1 secrets are still encrypted with older keys", log, StringComparison.Ordinal);
    }

    private static void HoldReencryption(IServiceCollection services)
    {
        services.RemoveAll<IJobRunner>();
        services.AddSingleton<IJobRunner, NeverRuns>();
    }

    private static IDataProtector Protector(AppFactory app) =>
        app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static Task<long> RotationRecordsAsync(string database) =>
        Sql.ScalarAsync<long>(database, "SELECT count(*) FROM audit_events WHERE action = 'MasterKeyRotated'", Cancellation);
}
