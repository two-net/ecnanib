using System.Net;
using System.Security.Cryptography;
using Ecnanib.Accounts;
using Ecnanib.Platform.Security;
using Ecnanib.Platform.Settings;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The two-factor secret is stored encrypted (FR-ACC-3, NFR-SEC-5): with authenticated encryption
/// under a master key of the configuration file, and with the id of that key beside it
/// (FR-KEY-6, FR-KEY-7). The file of the tests lists the keys k1 and k2, and marks k2 active.
/// </summary>
public class TwoFactorSecretStorageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_key_is_in_the_database_only_encrypted_with_the_id_of_the_active_master_key()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);

        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        // A nonce of 12 bytes, the 20 bytes of the key encrypted, and a tag of 16, with the id
        // of the master key that encrypted them.
        var stored = StoredTwoFactor.Of(application.Database, Ada);
        Assert.Equal("k2", stored.SecretKeyId);
        Assert.Equal(12 + 20 + 16, stored.Secret!.Length);
        Assert.False(stored.Secret.AsSpan().IndexOf(app.Key) >= 0);

        // Nothing in the database, in any column of any table, holds the key in a form that
        // an app or a reader could use.
        var everything = application.EverythingStored();
        Assert.Contains(Convert.ToHexString(stored.Secret), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(app.Key), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.KeyText, everything);
        Assert.DoesNotContain(Convert.ToBase64String(app.Key), everything);
    }

    [Fact]
    public async Task The_stored_key_decrypts_with_the_master_key_and_with_no_other()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (user, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);

        var stored = StoredTwoFactor.Of(application.Database, Ada);
        var value = new EncryptedSecret(stored.SecretKeyId!, stored.Secret!);
        var boundTo = $"two-factor:{user.Id}";

        // With the key that the file lists under that id, it is the key of the app.
        Assert.Equal(app.Key, Secrets("k2", Convert.FromBase64String(SettingsJson.MasterKey)).Decrypt(value, boundTo));

        // With another key under that id, or as the value of another account, it is nothing.
        Assert.ThrowsAny<CryptographicException>(() => Secrets("k2", new byte[32]).Decrypt(value, boundTo));
        Assert.ThrowsAny<CryptographicException>(
            () => Secrets("k2", Convert.FromBase64String(SettingsJson.MasterKey)).Decrypt(value, $"two-factor:{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task Two_accounts_that_were_given_the_same_key_would_not_store_the_same_value()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        await application.RegisterWithTwoFactorAsync(Ada, clock);
        await application.RegisterWithTwoFactorAsync(Grace, clock);

        // Each value has a nonce of its own, so nothing of one is in the other.
        var ada = StoredTwoFactor.Of(application.Database, Ada).Secret!;
        var grace = StoredTwoFactor.Of(application.Database, Grace).Secret!;

        Assert.NotEqual(Convert.ToHexString(ada.AsSpan(0, 12)), Convert.ToHexString(grace.AsSpan(0, 12)));
        Assert.NotEqual(Convert.ToHexString(ada.AsSpan(12)), Convert.ToHexString(grace.AsSpan(12)));
    }

    [Fact]
    public async Task The_encrypted_key_of_one_account_does_not_work_in_the_row_of_another()
    {
        // As a deployment runs, where an error is answered with the error page.
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock, host: host => host.UseEnvironment("Production"));
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        await application.RegisterWithTwoFactorAsync(Grace, clock);

        // Someone who can write to the database gives another account the encrypted key of
        // their own, for which they have the app.
        application.Database.Execute(
            $"""
            UPDATE users
            SET two_factor_secret = (SELECT two_factor_secret FROM users WHERE email = '{Ada}'),
                two_factor_secret_key_id = (SELECT two_factor_secret_key_id FROM users WHERE email = '{Ada}')
            WHERE email = '{Grace}'
            """);

        using var client = application.CreateClient();
        await client.SignInAsync(Grace);
        using var answer = await client.SendFormAsync(
            SessionAuthentication.SignInCodePath,
            "sign-in-code",
            Browser.AntiforgeryTokenIn(await client.PageAsync(SessionAuthentication.SignInCodePath)),
            ("Code", app.CodeAt(clock.GetUtcNow())));

        // The value is not the other account's, so it does not decrypt: an error, and nobody is signed in.
        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Empty(StoredSession.All(application.Database));
        Assert.Contains(application.Events(), written => written.Level == "Error" && written.Exception is not null);
    }

    [Fact]
    public async Task A_key_encrypted_under_an_earlier_master_key_still_signs_in_after_the_active_one_changes()
    {
        var clock = new TestClock(Morning);
        var database = PostgresServer.CreateDatabase();
        AuthenticatorApp app;

        // The file marks k1 active when two-factor authentication is turned on.
        using (var earlier = new AccountsApplication(
            clock: clock, database: database, settings: file => file.With("Security:ActiveKeyId", "k1")))
        {
            (_, app, _) = await earlier.RegisterWithTwoFactorAsync(Ada, clock);

            Assert.Equal("k1", StoredTwoFactor.Of(database, Ada).SecretKeyId);
        }

        // Then the file marks k2 active, and still lists k1.
        using var application = new AccountsApplication(clock: clock, database: database);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // The value names its key, so it is still read.
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
        Assert.Equal("k1", StoredTwoFactor.Of(database, Ada).SecretKeyId);

        // A key that is stored from now on is encrypted with the active one.
        await application.RegisterWithTwoFactorAsync(Grace, clock);
        Assert.Equal("k2", StoredTwoFactor.Of(database, Grace).SecretKeyId);
    }

    [Fact]
    public async Task Turning_two_factor_off_removes_the_key()
    {
        var clock = new TestClock(Morning);
        using var application = new AccountsApplication(clock: clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        var secret = StoredTwoFactor.Of(application.Database, Ada).Secret!;

        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", app.CodeAt(clock.GetUtcNow())));

        // Not the encrypted key either, anywhere.
        Assert.True(StoredTwoFactor.Of(application.Database, Ada).IsEmpty);
        Assert.DoesNotContain(Convert.ToHexString(secret), application.EverythingStored(), StringComparison.OrdinalIgnoreCase);
    }

    private static StoredSecrets Secrets(string id, byte[] key) =>
        new(new SecuritySettings(
            id,
            new Dictionary<string, Secret>(StringComparer.OrdinalIgnoreCase) { [id] = new(Convert.ToBase64String(key)) },
            SessionIdleMinutes: 45));
}
