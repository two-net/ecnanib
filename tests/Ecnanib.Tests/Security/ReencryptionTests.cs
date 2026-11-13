using System.Data.Common;
using System.Net;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Security;

/// <summary>
/// Master keys can be rotated: a re-encryption routine moves all stored values to the active key
/// (FR-KEY-7). It runs when the application starts, after the database has been migrated and
/// before anything is served. The file of the tests lists the keys k1 and k2, and marks k2
/// active. What is stored encrypted today is the key of two-factor authentication.
/// </summary>
public class ReencryptionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.Platform.Security.StoredSecretReencryptor";
    private const string Column = "users.two_factor_secret";

    private const string Moved = "Encrypted {ValueCount} stored secrets of {Column} again, with the active master key {ActiveKeyId}";
    private const string AllUnderTheActiveKey = "Every stored secret is encrypted with the active master key {ActiveKeyId}";
    private const string KeysNotNeeded =
        "No stored secret is encrypted with the master keys {KeyIds}, so they can be taken out of the configuration file";
    private const string SomeUnderOtherKeys =
        "{ValueCount} stored secrets are still encrypted with another master key than the active one, {ActiveKeyId}. " +
        "Keep the master keys they name in the configuration file";
    private const string NamesAnUnlistedKey =
        "{ValueCount} stored secrets of {Column} name the master key {KeyId}, which the configuration file does not list. " +
        "They cannot be read until it lists that key again";
    private const string DoesNotDecrypt =
        "{ValueCount} stored secrets of {Column} do not decrypt with the master key {KeyId}, which they name. They were " +
        "changed, or the configuration file lists another key under that id. They are left as they are";

    private static readonly DateTimeOffset Morning = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly byte[] K1 = new byte[32];
    private static readonly byte[] K2 = Convert.FromBase64String(SettingsJson.MasterKey);

    [Fact]
    public async Task After_the_active_key_changes_the_next_start_encrypts_every_stored_key_with_it()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        var apps = await StoreUnderK1Async(database, clock, Ada, Grace);

        var before = StoredTwoFactor.Of(database, Ada);
        Assert.Equal("k1", before.SecretKeyId);

        // The file marks k2 active now, and still lists k1.
        using var application = new AccountsApplication(clock: clock, database: database);
        Start(application);

        foreach (var (email, app) in apps)
        {
            var stored = StoredTwoFactor.Of(database, email);
            var boundTo = $"two-factor:{StoredUser.Of(database, email)!.Id}";

            // The same key of the same account, as k2 alone encrypts it.
            Assert.Equal("k2", stored.SecretKeyId);
            Assert.Equal(12 + 20 + 16, stored.Secret!.Length);
            Assert.Equal(app.Key, Secrets("k2", K2).Decrypt(new EncryptedSecret("k2", stored.Secret), boundTo));
        }

        // With a nonce of its own: nothing of the earlier value is left.
        var after = StoredTwoFactor.Of(database, Ada);
        Assert.NotEqual(Convert.ToHexString(before.Secret!.AsSpan(0, 12)), Convert.ToHexString(after.Secret!.AsSpan(0, 12)));
        Assert.NotEqual(Convert.ToHexString(before.Secret.AsSpan(12)), Convert.ToHexString(after.Secret.AsSpan(12)));
        Assert.DoesNotContain(Convert.ToHexString(before.Secret!), application.EverythingStored(), StringComparison.OrdinalIgnoreCase);

        // Nothing else about the account has changed, and its app still signs in.
        Assert.Equal(before with { Secret = after.Secret, SecretKeyId = "k2" }, after);

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        Assert.Equal("/", (await client.EnterCodeAsync(apps[Ada].CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task The_log_says_what_was_encrypted_again_and_that_the_earlier_key_is_no_longer_needed()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        var apps = await StoreUnderK1Async(database, clock, Ada, Grace);

        using var application = new AccountsApplication(clock: clock, database: database);
        Start(application);

        var moved = Assert.Single(Written(application), written => written.Template == Moved);
        Assert.Equal("Information", moved.Level);
        Assert.Equal(2, moved["ValueCount"].GetInt32());
        Assert.Equal(Column, moved.Text("Column"));
        Assert.Equal("k2", moved.Text("ActiveKeyId"));

        var all = Assert.Single(Written(application), written => written.Template == AllUnderTheActiveKey);
        Assert.Equal("k2", all.Text("ActiveKeyId"));

        var notNeeded = Assert.Single(Written(application), written => written.Template == KeysNotNeeded);
        Assert.Equal(["k1"], notNeeded["KeyIds"].EnumerateArray().Select(keyId => keyId.GetString()));

        Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");

        // Neither a key of an account nor a master key, in any form.
        var log = application.LogText();
        foreach (var app in apps.Values)
        {
            Assert.DoesNotContain(Convert.ToHexString(app.Key), log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(app.Key), log);
            Assert.DoesNotContain(app.KeyText, log);
        }

        Assert.DoesNotContain(Convert.ToBase64String(K1), log);
        Assert.DoesNotContain(Convert.ToBase64String(K2), log);
    }

    [Fact]
    public async Task Once_everything_is_encrypted_with_the_active_key_the_earlier_one_can_leave_the_file()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        var apps = await StoreUnderK1Async(database, clock, Ada);

        // The start that encrypts everything again.
        using (var rotating = new AccountsApplication(clock: clock, database: database))
        {
            Start(rotating);
        }

        // Then the file lists k2 alone.
        using var application = new AccountsApplication(
            clock: clock, database: database, settings: file => file.Without("Security:MasterKeys:k1"));
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        Assert.Equal("/", (await client.EnterCodeAsync(apps[Ada].CodeAt(clock.GetUtcNow()))).Path);

        // There is no other key in the file to say anything about.
        Assert.Single(Written(application), written => written.Template == AllUnderTheActiveKey);
        Assert.DoesNotContain(Written(application), written => written.Template == KeysNotNeeded);
        Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task A_start_with_nothing_to_encrypt_again_writes_nothing()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();

        using (var first = new AccountsApplication(clock: clock, database: database))
        {
            await first.RegisterWithTwoFactorAsync(Ada, clock);
            await first.RegisterVerifiedAsync(Grace);
        }

        var before = StoredTwoFactor.Of(database, Ada);

        using var application = new AccountsApplication(clock: clock, database: database);
        Start(application);

        // Not even the same key under a new nonce.
        var after = StoredTwoFactor.Of(database, Ada);
        Assert.Equal("k2", after.SecretKeyId);
        Assert.Equal(before.Secret, after.Secret);

        // An account without two-factor authentication stores nothing, before and after.
        Assert.True(StoredTwoFactor.Of(database, Grace).IsEmpty);

        Assert.DoesNotContain(Written(application), written => written.Template == Moved);
        Assert.Single(Written(application), written => written.Template == AllUnderTheActiveKey);
    }

    [Fact]
    public void The_first_start_on_an_empty_database_finds_nothing_and_says_so()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        var events = application.Events().Where(written => written.Area == Area).ToList();
        Assert.Equal([AllUnderTheActiveKey, KeysNotNeeded], events.Select(written => written.Template));
        Assert.All(events, written => Assert.Equal("Information", written.Level));
    }

    [Fact]
    public async Task A_value_that_names_a_key_which_the_file_does_not_list_is_left_and_reported_and_the_others_are_encrypted_again()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        var apps = await StoreUnderK1Async(database, clock, Ada, Grace);

        // As after a key was taken out of the file too early: the value of one account names
        // a key that the file does not list.
        database.Execute($"UPDATE users SET two_factor_secret_key_id = 'k0' WHERE email = '{Ada}'");
        var before = StoredTwoFactor.Of(database, Ada);

        using var application = new AccountsApplication(clock: clock, database: database);
        using var client = application.CreateClient();

        // The application starts all the same, and serves.
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var after = StoredTwoFactor.Of(database, Ada);
        Assert.Equal("k0", after.SecretKeyId);
        Assert.Equal(before.Secret, after.Secret);
        Assert.Equal("k2", StoredTwoFactor.Of(database, Grace).SecretKeyId);

        var unlisted = Assert.Single(Written(application), written => written.Template == NamesAnUnlistedKey);
        Assert.Equal("Error", unlisted.Level);
        Assert.Equal(1, unlisted["ValueCount"].GetInt32());
        Assert.Equal(Column, unlisted.Text("Column"));
        Assert.Equal("k0", unlisted.Text("KeyId"));

        Assert.Equal(1, Assert.Single(Written(application), written => written.Template == Moved)["ValueCount"].GetInt32());

        // So the log does not say that k1 can go, nor that everything is under k2.
        var some = Assert.Single(Written(application), written => written.Template == SomeUnderOtherKeys);
        Assert.Equal("Warning", some.Level);
        Assert.Equal(1, some["ValueCount"].GetInt32());
        Assert.Equal("k2", some.Text("ActiveKeyId"));
        Assert.DoesNotContain(Written(application), written => written.Template is AllUnderTheActiveKey or KeysNotNeeded);

        // The other account signs in as before.
        await client.SignInAsync(Grace);
        Assert.Equal("/", (await client.EnterCodeAsync(apps[Grace].CodeAt(clock.GetUtcNow()))).Path);
    }

    [Fact]
    public async Task A_value_that_was_changed_is_left_and_reported_and_the_others_are_encrypted_again()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        await StoreUnderK1Async(database, clock, Ada, Grace);

        // One bit of the stored value of one account, in its ciphertext.
        database.Execute(
            $"UPDATE users SET two_factor_secret = set_byte(two_factor_secret, 20, get_byte(two_factor_secret, 20) # 1) WHERE email = '{Ada}'");
        var before = StoredTwoFactor.Of(database, Ada);

        using var application = new AccountsApplication(clock: clock, database: database);
        Start(application);

        // It is not made to look like a value that the active key encrypted.
        var after = StoredTwoFactor.Of(database, Ada);
        Assert.Equal("k1", after.SecretKeyId);
        Assert.Equal(before.Secret, after.Secret);
        Assert.Equal("k2", StoredTwoFactor.Of(database, Grace).SecretKeyId);

        var changed = Assert.Single(Written(application), written => written.Template == DoesNotDecrypt);
        Assert.Equal("Error", changed.Level);
        Assert.Equal(1, changed["ValueCount"].GetInt32());
        Assert.Equal(Column, changed.Text("Column"));
        Assert.Equal("k1", changed.Text("KeyId"));

        Assert.Single(Written(application), written => written.Template == SomeUnderOtherKeys);
        Assert.DoesNotContain(Written(application), written => written.Template is AllUnderTheActiveKey or KeysNotNeeded);
    }

    [Fact]
    public async Task A_value_of_one_account_in_the_row_of_another_is_not_encrypted_again()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        await StoreUnderK1Async(database, clock, Ada, Grace);

        // Someone who can write to the database gives another account the encrypted key of
        // their own, for which they have the app.
        database.Execute(
            $"""
            UPDATE users
            SET two_factor_secret = (SELECT two_factor_secret FROM users WHERE email = '{Ada}')
            WHERE email = '{Grace}'
            """);
        var planted = StoredTwoFactor.Of(database, Grace);

        using var application = new AccountsApplication(clock: clock, database: database);
        Start(application);

        // Encrypting it again would bind it to the account it was planted on.
        Assert.Equal("k2", StoredTwoFactor.Of(database, Ada).SecretKeyId);
        Assert.Equal("k1", StoredTwoFactor.Of(database, Grace).SecretKeyId);
        Assert.Equal(planted.Secret, StoredTwoFactor.Of(database, Grace).Secret);
        Assert.Single(Written(application), written => written.Template == DoesNotDecrypt);
    }

    [Fact]
    public async Task A_row_that_changes_while_its_value_is_encrypted_again_keeps_what_it_holds_by_then()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        await StoreUnderK1Async(database, clock, Ada);

        // As when the user turns two-factor authentication off on another instance, between
        // the moment the value is read and the moment it would be written.
        var turnsOff = new BeforeTheFirstUpdate(() => database.Execute(
            $"""
            UPDATE users
            SET two_factor_secret = NULL, two_factor_secret_key_id = NULL, two_factor_enabled_at = NULL, two_factor_last_step = NULL
            WHERE email = '{Ada}'
            """));

        using var application = new AccountsApplication(
            clock: clock,
            database: database,
            host: host => host.ConfigureServices(services =>
                services.ConfigureDbContext<EcnanibDbContext>(options => options.AddInterceptors(turnsOff))));
        Start(application);

        // The key is not brought back.
        Assert.True(turnsOff.Happened);
        Assert.True(StoredTwoFactor.Of(database, Ada).IsEmpty);

        Assert.DoesNotContain(Written(application), written => written.Template == Moved);
        Assert.Single(Written(application), written => written.Template == AllUnderTheActiveKey);
        Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task Two_instances_that_start_together_leave_every_value_readable_under_the_active_key()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        var apps = await StoreUnderK1Async(database, clock, Ada, Grace);

        using var one = new AccountsApplication(clock: clock, database: database);
        using var two = new AccountsApplication(clock: clock, database: database);

        await Task.WhenAll(
                Task.Run(() => Start(one), TestContext.Current.CancellationToken),
                Task.Run(() => Start(two), TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Whichever of them wrote a value, it is the key of its account.
        foreach (var (email, app) in apps)
        {
            var stored = StoredTwoFactor.Of(database, email);
            var boundTo = $"two-factor:{StoredUser.Of(database, email)!.Id}";

            Assert.Equal("k2", stored.SecretKeyId);
            Assert.Equal(app.Key, Secrets("k2", K2).Decrypt(new EncryptedSecret("k2", stored.Secret!), boundTo));
        }

        Assert.All(new[] { one, two }, application =>
            Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal"));
    }

    [Fact]
    public async Task The_values_are_encrypted_again_before_anything_is_scheduled_or_served()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        await StoreUnderK1Async(database, clock, Ada, Grace);

        // A hosted service stands for a scheduler: it is started with the host, like every one.
        var seen = new SeenAtStart(database);
        using var application = new AccountsApplication(
            clock: clock,
            database: database,
            host: host => host.ConfigureServices(services => services.AddHostedService(_ => seen)));
        Start(application);

        Assert.Equal(["k2", "k2"], seen.KeyIds);
    }

    /// <summary>
    /// Turns two-factor authentication on for accounts while the file marks k1 active, and
    /// stops that application.
    /// </summary>
    /// <returns>The app of each account, by its address.</returns>
    private static async Task<Dictionary<string, AuthenticatorApp>> StoreUnderK1Async(
        TestDatabase database, TestClock clock, params string[] emails)
    {
        using var earlier = new AccountsApplication(
            clock: clock, database: database, settings: file => file.With("Security:ActiveKeyId", "k1"));

        var apps = new Dictionary<string, AuthenticatorApp>();
        foreach (var email in emails)
        {
            (_, apps[email], _) = await earlier.RegisterWithTwoFactorAsync(email, clock);

            Assert.Equal("k1", StoredTwoFactor.Of(database, email).SecretKeyId);
        }

        return apps;
    }

    /// <summary>Starts the application, which a test's first use of it does.</summary>
    private static void Start(AccountsApplication application) => _ = application.Contexts;

    /// <summary>What the routine wrote to the log.</summary>
    private static IReadOnlyList<LoggedEvent> Written(AccountsApplication application) =>
        [.. application.Events().Where(written => written.Area == Area)];

    private static StoredSecrets Secrets(string id, byte[] key) =>
        new(new SecuritySettings(
            id,
            new Dictionary<string, Secret>(StringComparer.OrdinalIgnoreCase) { [id] = new(Convert.ToBase64String(key)) },
            SessionIdleMinutes: 45));

    /// <summary>Records, as it starts, the master key that each stored value names by then.</summary>
    private sealed class SeenAtStart(TestDatabase database) : IHostedService
    {
        public IReadOnlyList<string>? KeyIds { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            KeyIds = database.Column("SELECT two_factor_secret_key_id FROM users WHERE two_factor_secret IS NOT NULL");

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Does something once, just before the first update that the application sends as one statement.</summary>
    private sealed class BeforeTheFirstUpdate(Action what) : DbCommandInterceptor
    {
        private int _happened;

        public bool Happened => Volatile.Read(ref _happened) == 1;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.CommandSource == CommandSource.ExecuteUpdate && Interlocked.Exchange(ref _happened, 1) == 0)
            {
                what();
            }

            return result;
        }
    }
}
