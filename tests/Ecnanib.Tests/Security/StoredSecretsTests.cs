using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Security;
using Ecnanib.Platform.Settings;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// A stored secret is encrypted with authenticated encryption, a 256-bit key, and a nonce of its
/// own, and it names the master key that encrypted it. Master keys come from the configuration
/// file, where one is marked active (FR-KEY-6, FR-KEY-7).
/// </summary>
public class StoredSecretsTests
{
    private const string Owner = "two-factor:0193a3c2-0000-7000-8000-000000000001";

    private static readonly byte[] Earlier = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly byte[] Later = [.. Enumerable.Range(101, 32).Select(i => (byte)i)];
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("SECRET-MARKER of some length");

    [Fact]
    public void A_secret_comes_back_as_it_went_in()
    {
        var secrets = Secrets("later", ("earlier", Earlier), ("later", Later));

        var stored = secrets.Encrypt(Secret, Owner);

        Assert.Equal(Secret, secrets.Decrypt(stored, Owner));

        // Also one of no length at all.
        Assert.Empty(secrets.Decrypt(secrets.Encrypt([], Owner), Owner));
    }

    [Fact]
    public void A_stored_value_does_not_hold_the_secret()
    {
        var stored = Secrets("later", ("later", Later)).Encrypt(Secret, Owner);

        Assert.DoesNotContain("SECRET-MARKER", Encoding.Latin1.GetString(stored.Value));
        Assert.False(stored.Value.AsSpan().IndexOf(Secret) >= 0);
    }

    [Fact]
    public void The_same_secret_is_stored_differently_each_time()
    {
        var secrets = Secrets("later", ("later", Later));

        var values = Enumerable.Range(0, 50).Select(_ => secrets.Encrypt(Secret, Owner).Value).ToList();

        // Each has a nonce of its own, so neither the nonce nor what follows it is shared.
        Assert.Equal(50, values.Select(value => Convert.ToHexString(value.AsSpan(0, 12))).Distinct().Count());
        Assert.Equal(50, values.Select(value => Convert.ToHexString(value.AsSpan(12))).Distinct().Count());
    }

    [Fact]
    public void A_value_names_the_master_key_that_encrypted_it_which_is_the_active_one()
    {
        var keys = new[] { ("earlier", Earlier), ("later", Later) };

        Assert.Equal("earlier", Secrets("earlier", keys).Encrypt(Secret, Owner).KeyId);
        Assert.Equal("later", Secrets("later", keys).Encrypt(Secret, Owner).KeyId);
    }

    [Fact]
    public void A_value_is_read_with_the_key_it_names_when_another_is_active()
    {
        var stored = Secrets("earlier", ("earlier", Earlier)).Encrypt(Secret, Owner);

        // The file has a new key since, and marks it active. The earlier one is still listed.
        var rotated = Secrets("later", ("earlier", Earlier), ("later", Later));

        Assert.Equal(Secret, rotated.Decrypt(stored, Owner));
        Assert.Equal("later", rotated.Encrypt(Secret, Owner).KeyId);
    }

    [Fact]
    public void The_id_of_a_key_ignores_case()
    {
        // The file names the active key in other letters than it lists it in.
        var secrets = Secrets("KEY-1", ("Key-1", Earlier));

        var stored = secrets.Encrypt(Secret, Owner);

        // The id is stored as the file lists the key, and read however it is written.
        Assert.Equal("Key-1", stored.KeyId);
        Assert.Equal(Secret, secrets.Decrypt(stored with { KeyId = "key-1" }, Owner));
    }

    [Fact]
    public void A_value_that_was_changed_is_refused()
    {
        var secrets = Secrets("later", ("later", Later));
        var stored = secrets.Encrypt(Secret, Owner);

        // Any one bit, in the nonce, in the ciphertext, or in the tag.
        for (var position = 0; position < stored.Value.Length; position++)
        {
            var changed = (byte[])stored.Value.Clone();
            changed[position] ^= 0x01;

            Assert.ThrowsAny<CryptographicException>(() => secrets.Decrypt(stored with { Value = changed }, Owner));
        }

        // And one that lost its end, or nearly everything.
        Assert.ThrowsAny<CryptographicException>(() => secrets.Decrypt(stored with { Value = stored.Value[..^1] }, Owner));
        Assert.ThrowsAny<CryptographicException>(() => secrets.Decrypt(stored with { Value = stored.Value[..20] }, Owner));
        Assert.ThrowsAny<CryptographicException>(() => secrets.Decrypt(stored with { Value = [] }, Owner));
    }

    [Fact]
    public void A_value_bound_to_one_owner_is_not_read_for_another()
    {
        var secrets = Secrets("later", ("later", Later));

        var stored = secrets.Encrypt(Secret, Owner);

        Assert.ThrowsAny<CryptographicException>(
            () => secrets.Decrypt(stored, "two-factor:0193a3c2-0000-7000-8000-000000000002"));
        Assert.ThrowsAny<CryptographicException>(() => secrets.Decrypt(stored, string.Empty));
    }

    [Fact]
    public void A_value_is_not_read_with_another_master_key()
    {
        var stored = Secrets("earlier", ("earlier", Earlier)).Encrypt(Secret, Owner);

        // The key the value names is no longer listed.
        Assert.ThrowsAny<CryptographicException>(() => Secrets("later", ("later", Later)).Decrypt(stored, Owner));

        // Or the id now belongs to another key.
        Assert.ThrowsAny<CryptographicException>(() => Secrets("earlier", ("earlier", Later)).Decrypt(stored, Owner));
    }

    [Fact]
    public void A_value_is_a_nonce_the_ciphertext_and_a_tag_as_AES_256_GCM_makes_them()
    {
        var stored = Secrets("later", ("later", Later)).Encrypt(Secret, Owner);

        // Read here with the algorithm itself, and the 256-bit key: a nonce of 12 bytes, as many
        // bytes of ciphertext as the secret has, and a tag of 16.
        Assert.Equal(12 + Secret.Length + 16, stored.Value.Length);

        var secret = new byte[Secret.Length];
        using var aes = new AesGcm(Later, tagSizeInBytes: 16);
        aes.Decrypt(
            stored.Value.AsSpan(0, 12),
            stored.Value.AsSpan(12, Secret.Length),
            stored.Value.AsSpan(12 + Secret.Length),
            secret,
            Encoding.UTF8.GetBytes(Owner));

        Assert.Equal(Secret, secret);
    }

    [Fact]
    public void The_application_encrypts_with_the_active_master_key_of_its_configuration_file()
    {
        // The file of the tests lists k1 and k2, and marks k2 active.
        using var application = new LoggedApplication(LoggedApplication.Settings());
        var secrets = application.Services.GetRequiredService<StoredSecrets>();

        var stored = secrets.Encrypt(Secret, Owner);

        Assert.Equal("k2", stored.KeyId);
        Assert.Equal(Secret, Secrets("k2", ("k2", Convert.FromBase64String(SettingsJson.MasterKey))).Decrypt(stored, Owner));
    }

    private static StoredSecrets Secrets(string activeKeyId, params (string Id, byte[] Key)[] keys) =>
        new(new SecuritySettings(
            activeKeyId,
            keys.ToDictionary(key => key.Id, key => new Secret(Convert.ToBase64String(key.Key)), StringComparer.OrdinalIgnoreCase),
            SessionIdleMinutes: 60));
}
