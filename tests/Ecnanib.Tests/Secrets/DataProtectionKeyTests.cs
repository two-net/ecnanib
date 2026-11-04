using System.Text;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Secrets;

/// <summary>
/// The keys that protect cookies and antiforgery tokens live in the database, with their secret part
/// encrypted under the master key, so a deployment is the application, its configuration file and
/// the database (NFR-DEP-01). See Secrets in docs/architecture.md.
/// </summary>
public sealed class DataProtectionKeyTests(PostgresFixture postgres)
{
    private const string Purpose = "Ecnanib.Tests.DataProtection";

    /// <summary>
    /// Every stored key names its decryptor by type, so moving or renaming that class would make every
    /// key unreadable and sign everyone out. Change this only with a migration that rewrites the rows.
    /// </summary>
    private const string Decryptor = """decryptorType="Ecnanib.Platform.Secrets.DataProtection.SecretStoreXmlDecryptor, Ecnanib,""";

    /// <summary>The element that takes the key material's place. It has no namespace, unlike its parent.</summary>
    private const string SecretReference = """<secret id="(?<id>[0-9a-f-]{36})" xmlns="" />""";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_key_ring_holds_no_key_material_only_a_secret_to_find_it_by()
    {
        var database = postgres.NewConnectionString();

        await using (var app = new AppFactory(database))
        {
            app.Start();
        }

        var keys = await Sql.ColumnAsync(database, "SELECT xml FROM data_protection_keys", Cancellation);

        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            Assert.Contains(Decryptor, xml, StringComparison.Ordinal);
            Assert.Matches(SecretReference, xml);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<value>", xml, StringComparison.Ordinal);
        });

        var referenced = keys.Select(xml => System.Text.RegularExpressions.Regex.Match(xml, SecretReference).Groups["id"].Value);

        foreach (var id in referenced)
        {
            Assert.Equal(AppFactory.TestKeyId, await SecretApp.KeyIdAsync(database, Guid.Parse(id)));
        }
    }

    /// <summary>A restart, or a move to another host, keeps everyone signed in.</summary>
    [Fact]
    public async Task Another_instance_on_the_same_database_reads_what_the_first_protected()
    {
        var database = postgres.NewConnectionString();
        var text = Guid.NewGuid().ToString();
        byte[] payload;

        await using (var first = new AppFactory(database))
        {
            payload = Protector(first).Protect(Encoding.UTF8.GetBytes(text));
        }

        var keys = await KeyCountAsync(database);

        await using var second = new AppFactory(database);

        Assert.Equal(text, Encoding.UTF8.GetString(Protector(second).Unprotect(payload)));
        Assert.Equal(keys, await KeyCountAsync(database));
    }

    private static IDataProtector Protector(AppFactory app) =>
        app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static Task<long> KeyCountAsync(string database) =>
        Sql.ScalarAsync<long>(database, "SELECT count(*) FROM data_protection_keys", Cancellation);
}
