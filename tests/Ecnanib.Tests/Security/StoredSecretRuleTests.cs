using System.ComponentModel.DataAnnotations.Schema;
using Ecnanib.Platform.Security;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// A re-encryption routine moves all stored values to the active master key (FR-KEY-7). These
/// tests hold the columns of the model to <see cref="StoredSecretRules"/>, so that a stored
/// secret which is added later is one that the routine moves.
/// </summary>
public class StoredSecretRuleTests
{
    [Fact]
    public void Every_column_that_names_a_master_key_belongs_to_a_stored_secret_that_is_declared()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var context = ModelOnly.Context();

        var violations = StoredSecretRules.FindViolations(context.Model, Declared(application));

        Assert.True(
            violations.Count == 0,
            "Every stored secret is declared, so that it is encrypted again when the active master key changes. See docs/security.md:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void What_is_stored_encrypted_is_the_key_of_two_factor_authentication()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var context = ModelOnly.Context();

        // Pinned, so that a stored secret which is added, or one that is no longer declared,
        // shows in a review as what it is.
        Assert.Equal(["users.two_factor_secret"], StoredSecretRules.ColumnsOf(context.Model, Declared(application)));
        Assert.Empty(StoredSecretRules.NotTheIdOfAMasterKey);
    }

    [Fact]
    public void A_stored_secret_that_is_not_declared_or_is_declared_wrongly_is_found()
    {
        using var context = new PlantedContext();

        StoredSecretColumn[] declared =
        [
            // Fine: both columns are stored, and both may be empty.
            new StoredSecretColumn<Vault>(vault => vault.Secret, vault => vault.SecretKeyId, vault => $"vault:{vault.Id}"),

            // Not fine: a second time.
            new StoredSecretColumn<Vault>(vault => vault.Secret, vault => vault.SecretKeyId, vault => $"vault:{vault.Id}"),

            // Not fine: under the id of another value's master key.
            new StoredSecretColumn<Vault>(vault => vault.Other, vault => vault.SecretKeyId, vault => $"vault-other:{vault.Id}"),

            // Not fine: the value is required, and the id of its master key is not.
            new StoredSecretColumn<Locker>(locker => locker.Code, locker => locker.CodeKeyId, locker => $"locker:{locker.Id}"),

            // Not fine: neither column is stored.
            new StoredSecretColumn<Locker>(locker => locker.Spare, locker => locker.SpareKeyId, locker => $"locker-spare:{locker.Id}"),

            // Not fine: not an entity of the model.
            new StoredSecretColumn<Stranger>(stranger => stranger.Secret, stranger => stranger.SecretKeyId, _ => "stranger"),
        ];

        var listed = new Dictionary<string, string>
        {
            ["doors.lock_key_id"] = "Fine: the id of a key of a door.",
            ["doors.hinge_key_id"] = "Not fine: no such column.",
            ["doors.color"] = "Not fine: no such column.",
            ["vaults.secret_key_id"] = "Not fine: a stored secret is declared beside it.",
        };

        Assert.Equal(
            [
                "Locker.Spare: Locker.SpareKeyId is declared to name its master key, but is not stored. Map it as a column.",
                "Locker.Spare: is declared to hold a stored secret, but is not stored. Map it as a column.",
                "Stranger.Secret: is declared to hold a stored secret, but Stranger is not an entity of the model. Take the declaration away.",
                "doors.color: is listed, but the model has no column of that name that ends in \"key id\". Take it off the list.",
                "doors.hinge_key_id: is listed, but the model has no column of that name that ends in \"key id\". Take it off the list.",
                "drawers.token_key_id: is named as the id of a master key, and no stored secret is declared beside it. Declare the column of the value with AddStoredSecret, or list this one in StoredSecretRules.NotTheIdOfAMasterKey with what it holds.",
                "lockers.code: may be empty where lockers.code_key_id may not, or the other way round. A value and the id of its master key are there together. Make both required, or neither.",
                "vaults.other: vaults.secret_key_id names its master key, and that of another stored secret. Give each stored secret a column of its own for the id of its master key.",
                "vaults.secret: is declared to hold a stored secret more than once. Declare it once.",
                "vaults.secret_key_id: is listed as not the id of a master key, but a stored secret is declared beside it. Take it off the list.",
            ],
            StoredSecretRules.FindViolations(context.Model, declared, listed));
    }

    [Fact]
    public void A_declaration_names_a_property_of_the_entity()
    {
        var column = new StoredSecretColumn<Vault>(vault => vault.Secret, vault => vault.SecretKeyId, vault => $"vault:{vault.Id}");

        Assert.Equal(typeof(Vault), column.Entity);
        Assert.Equal(nameof(Vault.Secret), column.Value);
        Assert.Equal(nameof(Vault.SecretKeyId), column.KeyId);

        // Not something else that happens to be bytes or text.
        Assert.Throws<ArgumentException>(
            () => new StoredSecretColumn<Vault>(_ => new byte[1], vault => vault.SecretKeyId, _ => "vault"));
        Assert.Throws<ArgumentException>(
            () => new StoredSecretColumn<Vault>(vault => vault.Secret, vault => vault.SecretKeyId + "!", _ => "vault"));
    }

    private static IReadOnlyList<StoredSecretColumn> Declared(LoggedApplication application) =>
        [.. application.Services.GetServices<StoredSecretColumn>()];

    /// <summary>
    /// A model with deliberate violations, and with columns the rule leaves alone. Its columns
    /// are named as the application names them.
    /// </summary>
    private sealed class PlantedContext()
        : DbContext(new DbContextOptionsBuilder().UseNpgsql().UseSnakeCaseNamingConvention().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vault>().ToTable("vaults");
            modelBuilder.Entity<Locker>().ToTable("lockers");
            modelBuilder.Entity<Drawer>().ToTable("drawers");
            modelBuilder.Entity<Door>().ToTable("doors");
        }
    }

    private sealed class Vault
    {
        public Guid Id { get; set; }

        public byte[]? Secret { get; set; }

        public string? SecretKeyId { get; set; }

        public byte[]? Other { get; set; }
    }

    private sealed class Locker
    {
        public Guid Id { get; set; }

        public byte[] Code { get; set; } = [];

        public string? CodeKeyId { get; set; }

        [NotMapped]
        public byte[]? Spare { get; set; }

        [NotMapped]
        public string? SpareKeyId { get; set; }
    }

    /// <summary>Holds a secret that nobody declared.</summary>
    private sealed class Drawer
    {
        public Guid Id { get; set; }

        public byte[]? Token { get; set; }

        public string? TokenKeyId { get; set; }
    }

    private sealed class Door
    {
        public Guid Id { get; set; }

        public string LockKeyId { get; set; } = string.Empty;

        public string Color { get; set; } = string.Empty;
    }

    private sealed class Stranger
    {
        public byte[]? Secret { get; set; }

        public string? SecretKeyId { get; set; }
    }
}
