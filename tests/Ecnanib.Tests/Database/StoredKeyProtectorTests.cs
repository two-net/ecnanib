using System.Security.Cryptography;
using System.Text;
using Ecnanib.Database;
using Ecnanib.Database.Configuration;
using Ecnanib.Database.Entities;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Database;

/// <summary>FR-KEY-04, A-02: a stored key opens in its own cell only, under the configured key only, and untouched only.</summary>
public class StoredKeyProtectorTests
{
    private const string Value = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd";
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(StoredKeyProtector.KeySize);
    private static readonly byte[] OtherKey = RandomNumberGenerator.GetBytes(StoredKeyProtector.KeySize);

    [Theory]
    [InlineData(StoredKeyColumn.ExchangeApiKey)]
    [InlineData(StoredKeyColumn.ExchangeApiSecret)]
    [InlineData(StoredKeyColumn.ProviderApiKey)]
    public void RoundTripsInItsOwnCellWithAFreshNoncePerWrite(StoredKeyColumn column)
    {
        var protector = new StoredKeyProtector(Key, null);
        var id = Guid.CreateVersion7();

        var first = protector.Protect(column, id, Value);
        var second = protector.Protect(column, id, Value);

        Assert.Equal(Value.Length + StoredKeyProtector.Overhead, first.Length);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(Value, Encoding.Latin1.GetString(first), StringComparison.Ordinal);
        Assert.Equal(Value, protector.Unprotect(column, id, StoredKeyCipher.AesGcm, first));
        Assert.Equal(Value, protector.Unprotect(column, id, StoredKeyCipher.AesGcm, second));
    }

    [Fact]
    public void OpensNowhereElse()
    {
        var protector = new StoredKeyProtector(Key, null);
        var id = Guid.CreateVersion7();
        var stored = protector.Protect(StoredKeyColumn.ExchangeApiKey, id, Value);

        // Another column, another row, another key, one changed byte, a truncated value, a row still plain.
        Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(StoredKeyColumn.ExchangeApiSecret, id, StoredKeyCipher.AesGcm, stored));
        Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(StoredKeyColumn.ExchangeApiKey, Guid.CreateVersion7(), StoredKeyCipher.AesGcm, stored));
        Assert.Throws<StoredKeyProtectionException>(() => new StoredKeyProtector(OtherKey, null).Unprotect(StoredKeyColumn.ExchangeApiKey, id, StoredKeyCipher.AesGcm, stored));
        var tampered = (byte[])stored.Clone();
        tampered[^1] ^= 0x01;
        Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(StoredKeyColumn.ExchangeApiKey, id, StoredKeyCipher.AesGcm, tampered));
        Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(StoredKeyColumn.ExchangeApiKey, id, StoredKeyCipher.AesGcm, stored[..10]));
        var plain = Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(StoredKeyColumn.ExchangeApiKey, id, StoredKeyCipher.Plain, Encoding.UTF8.GetBytes(Value)));
        Assert.Contains("Plain", plain.Message);
    }

    /// <summary>FR-KEY-04, FR-LOG-05: nothing a refusal says carries the value or a key.</summary>
    [Fact]
    public void RefusalsNameNeitherTheValueNorTheKeys()
    {
        var protector = new StoredKeyProtector(Key, null);
        var id = Guid.CreateVersion7();
        var stored = protector.Protect(StoredKeyColumn.ProviderApiKey, id, Value);

        var ex = Assert.Throws<StoredKeyProtectionException>(() => new StoredKeyProtector(OtherKey, null).Unprotect(StoredKeyColumn.ProviderApiKey, id, StoredKeyCipher.AesGcm, stored));

        var text = ex.ToString();
        Assert.DoesNotContain(Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Key), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(OtherKey), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(stored), text, StringComparison.Ordinal);
    }

    /// <summary>The rotation path: the previous key opens what it sealed, and the check value tells the two keys apart.</summary>
    [Fact]
    public void ThePreviousKeyOpensWhatItSealedAndTheCheckValueTellsTheKeysApart()
    {
        var old = new StoredKeyProtector(OtherKey, null);
        var rotating = new StoredKeyProtector(Key, OtherKey);
        var id = Guid.CreateVersion7();
        var stored = old.Protect(StoredKeyColumn.ProviderApiKey, id, Value);
        var check = old.ProtectCheckValue("purpose");

        Assert.True(rotating.HasPreviousKey);
        Assert.False(old.HasPreviousKey);
        Assert.Equal(Value, rotating.UnprotectWithPrevious(StoredKeyColumn.ProviderApiKey, id, stored));
        Assert.Throws<StoredKeyProtectionException>(() => rotating.Unprotect(StoredKeyColumn.ProviderApiKey, id, StoredKeyCipher.AesGcm, stored));
        Assert.Throws<InvalidOperationException>(() => old.UnprotectWithPrevious(StoredKeyColumn.ProviderApiKey, id, stored));
        Assert.False(rotating.CheckValueOpens("purpose", check, withPrevious: false));
        Assert.True(rotating.CheckValueOpens("purpose", check, withPrevious: true));
        Assert.False(rotating.CheckValueOpens("another purpose", check, withPrevious: true));
        Assert.False(old.CheckValueOpens("purpose", check, withPrevious: true));
        Assert.True(rotating.CheckValueOpens("purpose", rotating.ProtectCheckValue("purpose"), withPrevious: false));
        Assert.False(rotating.CheckValueOpens("purpose", [1, 2, 3], withPrevious: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEA==")]
    public void AKeyIsTheBase64OfExactly32Bytes(string? text)
    {
        Assert.False(StoredKeyProtector.TryParseKey(text, out _));

        Assert.True(StoredKeyProtector.TryParseKey("  " + DatabaseTestHost.EncryptionKey + "  ", out var key));
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (byte)i), key);
    }

    /// <summary>The application's constructor reads both keys from the Database section; the validator has already refused a malformed one.</summary>
    [Fact]
    public void ReadsItsKeysFromTheDatabaseSection()
    {
        var protector = new StoredKeyProtector(Options.Create(new DatabaseOptions { ConnectionString = "x", EncryptionKey = Convert.ToBase64String(Key), PreviousEncryptionKey = Convert.ToBase64String(OtherKey) }));
        var id = Guid.CreateVersion7();

        Assert.True(protector.HasPreviousKey);
        Assert.Equal(Value, protector.Unprotect(StoredKeyColumn.ProviderApiKey, id, StoredKeyCipher.AesGcm, new StoredKeyProtector(Key, null).Protect(StoredKeyColumn.ProviderApiKey, id, Value)));
        Assert.Equal(Value, protector.UnprotectWithPrevious(StoredKeyColumn.ProviderApiKey, id, new StoredKeyProtector(OtherKey, null).Protect(StoredKeyColumn.ProviderApiKey, id, Value)));
        Assert.False(new StoredKeyProtector(Options.Create(new DatabaseOptions { ConnectionString = "x", EncryptionKey = Convert.ToBase64String(Key), PreviousEncryptionKey = " " })).HasPreviousKey);
        Assert.Throws<ArgumentException>(() => new StoredKeyProtector(Options.Create(new DatabaseOptions { ConnectionString = "x", EncryptionKey = "short" })));
    }

    /// <summary>The entity extensions write every column of a row under its own id and stamp it encrypted.</summary>
    [Fact]
    public void TheEntityExtensionsProtectAndOpenWholeRows()
    {
        IStoredKeyProtector protector = new StoredKeyProtector(Key, null);
        var exchange = new ExchangeKey { Id = Guid.CreateVersion7(), Label = "main", ProtectedApiKey = [], ProtectedApiSecret = [] };
        var provider = new ProviderKey { Id = Guid.CreateVersion7(), ProtectedApiKey = [] };

        protector.Protect(exchange, "key", "secret");
        protector.Protect(provider, "sk-1");

        Assert.Equal(StoredKeyCipher.AesGcm, exchange.Cipher);
        Assert.Equal(StoredKeyCipher.AesGcm, provider.Cipher);
        Assert.Equal(("key", "secret"), protector.Unprotect(exchange));
        Assert.Equal("sk-1", protector.Unprotect(provider));
        Assert.Throws<StoredKeyProtectionException>(() => protector.Unprotect(new ExchangeKey { Id = exchange.Id, Label = "main", ProtectedApiKey = exchange.ProtectedApiSecret, ProtectedApiSecret = exchange.ProtectedApiKey, Cipher = StoredKeyCipher.AesGcm }));
    }
}
