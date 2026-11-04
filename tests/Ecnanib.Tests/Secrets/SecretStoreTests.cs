using System.Security.Cryptography;
using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Secrets;

/// <summary>An application with a second, inactive key, so a row can claim either.</summary>
public sealed class TwoKeyApp(PostgresFixture postgres) : AppFactory(
    postgres.NewConnectionString(),
    settings: TestKeys.Settings((AppFactory.TestKeyId, AppFactory.TestKey, true), (TwoKeyApp.OtherKeyId, TestKeys.NewKey(), false)))
{
    public const string OtherKeyId = "other-key";
}

/// <summary>FR-KEY-07 and FR-KEY-09: secrets are sealed with the active key, bound to their row.</summary>
public sealed class SecretStoreTests(TwoKeyApp app) : IClassFixture<TwoKeyApp>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private string Database => SecretApp.ConnectionString(app);

    private ISecretStore Store => app.Services.GetRequiredService<ISecretStore>();

    [Fact]
    public async Task A_secret_comes_back_as_it_went_in()
    {
        var value = $"sk-ant-{CanaryCases.NewCanary()} with ünïcödé";

        var id = await SecretApp.AddAsync(app, value);

        Assert.Equal(value, await SecretApp.RevealAsync(app, id));
    }

    [Fact]
    public async Task A_stored_secret_is_sealed_with_the_active_key_and_holds_no_plaintext()
    {
        var value = CanaryCases.NewCanary();

        var id = await SecretApp.AddAsync(app, value);
        var stored = await SecretApp.CiphertextHexAsync(Database, id);

        Assert.Equal(AppFactory.TestKeyId, await SecretApp.KeyIdAsync(Database, id));
        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(value)), stored, StringComparison.Ordinal);
        Assert.Equal((1 + 12 + 16 + value.Length) * 2, stored.Length);
        Assert.StartsWith("01", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_secret_is_sealed_differently_each_time()
    {
        var value = CanaryCases.NewCanary();

        var first = await SecretApp.AddAsync(app, value);
        var second = await SecretApp.AddAsync(app, value);

        Assert.NotEqual(await SecretApp.CiphertextHexAsync(Database, first), await SecretApp.CiphertextHexAsync(Database, second));
    }

    [Fact]
    public async Task Changed_bytes_are_refused_without_revealing_anything()
    {
        var value = CanaryCases.NewCanary();
        var id = await SecretApp.AddAsync(app, value);

        await SecretApp.CorruptAsync(Database, id);

        var exception = await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.RevealAsync(id, Cancellation));
        Assert.Contains(id.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The row's ID is bound into its ciphertext, so someone who can write to the table can't move a key to another row.</summary>
    [Fact]
    public async Task A_ciphertext_moved_to_another_row_is_refused()
    {
        var mine = await SecretApp.AddAsync(app, CanaryCases.NewCanary());
        var theirs = await SecretApp.AddAsync(app, CanaryCases.NewCanary());

        await Sql.ExecuteAsync(
            Database,
            $"UPDATE secrets SET ciphertext = (SELECT ciphertext FROM secrets WHERE id = '{theirs}') WHERE id = '{mine}'",
            Cancellation);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.RevealAsync(mine, Cancellation));
    }

    [Fact]
    public async Task A_secret_claiming_another_configured_key_is_refused()
    {
        var id = await SecretApp.AddAsync(app, CanaryCases.NewCanary());

        await Sql.ExecuteAsync(Database, $"UPDATE secrets SET key_id = '{TwoKeyApp.OtherKeyId}' WHERE id = '{id}'", Cancellation);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.RevealAsync(id, Cancellation));
    }

    [Fact]
    public async Task A_secret_whose_key_isnt_configured_is_refused_by_name()
    {
        var id = await SecretApp.AddAsync(app, CanaryCases.NewCanary());

        await Sql.ExecuteAsync(Database, $"UPDATE secrets SET key_id = 'gone' WHERE id = '{id}'", Cancellation);

        var exception = await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.RevealAsync(id, Cancellation));
        Assert.Contains("\"gone\", which isn't configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_is_only_stored_when_the_caller_saves()
    {
        var contextFactory = app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();
        Guid id;

        await using (var context = await contextFactory.CreateDbContextAsync(Cancellation))
        {
            using var value = SecretValue.FromString(CanaryCases.NewCanary());
            id = Store.Add(context, value);
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.RevealAsync(id, Cancellation));
    }

    [Fact]
    public async Task A_removed_secret_is_gone()
    {
        var id = await SecretApp.AddAsync(app, CanaryCases.NewCanary());
        var contextFactory = app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();

        await using (var context = await contextFactory.CreateDbContextAsync(Cancellation))
        {
            await Store.RemoveAsync(context, id, Cancellation);
            await Store.RemoveAsync(context, Guid.NewGuid(), Cancellation);
        }

        Assert.Equal(0, await Sql.ScalarAsync<long>(Database, $"SELECT count(*) FROM secrets WHERE id = '{id}'", Cancellation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.RevealAsync(id, Cancellation));
    }

    [Fact]
    public async Task A_disposed_value_cant_be_stored()
    {
        var contextFactory = app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(Cancellation);

        var value = SecretValue.FromString(CanaryCases.NewCanary());
        value.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Store.Add(context, value));
    }
}
