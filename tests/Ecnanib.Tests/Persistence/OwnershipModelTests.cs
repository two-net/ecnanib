using System.Reflection;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Every resource belongs to exactly one user, and every user-owned row carries its owner
/// (FR-ACC-5, NFR-DAT-9). Each kind of them has a test that a row of another user is out of
/// reach (NFR-SEC-3). These tests read the model and need no database.
/// </summary>
public class OwnershipModelTests
{
    [Fact]
    public void Every_table_carries_its_owner_or_is_listed_as_a_table_of_the_service()
    {
        using var context = ModelOnly.Context();

        var violations = OwnershipRules.FindViolations(context.Model);

        Assert.True(
            violations.Count == 0,
            "Every row belongs to one user and carries that user, or belongs to the service. See docs/ownership.md:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void The_tables_of_users_and_of_the_service_are_the_ones_expected()
    {
        // Pinned, so that a change to either list shows in a review as what it is: a decision
        // about whose data something is.
        using var context = ModelOnly.Context();

        Assert.Equal(
            ["audit_events", "legal_acceptances", "pending_sign_ins", "sessions", "two_factor_recovery_codes"],
            OwnershipRules.UserOwnedTables(context.Model));
        Assert.Equal(
            ["ai_providers", "background_jobs", "prompt_templates", "roles"],
            OwnershipRules.ServiceTables.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_kind_of_row_that_belongs_to_a_user_has_a_test_that_it_is_out_of_reach_of_another_user()
    {
        using var context = ModelOnly.Context();
        var owned = OwnershipRules.UserOwnedTables(context.Model);

        var marked = typeof(OwnershipModelTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.GetCustomAttribute<ProvesOwnershipOfAttribute>() is not null)
            .ToList();

        // The mark is on a test, or it proves nothing.
        Assert.All(marked, method => Assert.True(
            method.GetCustomAttributes<FactAttribute>().Any(),
            $"{method.DeclaringType!.Name}.{method.Name} is marked as proving ownership and is not a test."));

        var proven = marked
            .SelectMany(method => method.GetCustomAttribute<ProvesOwnershipOfAttribute>()!.Tables)
            .ToHashSet();

        var unproven = owned.Where(table => !proven.Contains(table)).ToList();
        Assert.True(
            unproven.Count == 0,
            "These tables hold rows of users, and no test shows that a user cannot reach a row of another. Write one " +
            $"for each kind of resource, and mark it [ProvesOwnershipOf(\"table\")]: {string.Join(", ", unproven)}");

        var unknown = proven.Where(table => !owned.Contains(table)).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            unknown.Count == 0,
            $"Tests are marked as proving the ownership of tables that hold no rows of users: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void A_table_that_does_not_say_whose_its_rows_are_is_found()
    {
        using var context = new PlantedContext();

        var serviceTables = new Dictionary<string, string>
        {
            ["reference"] = "Fine: the rows of the service.",
            ["listed_and_owned"] = "Not fine: its rows carry an owner.",
            ["gone"] = "Not fine: no such table.",
        };

        Assert.Equal(
            [
                "gone: is listed as a table of the service, but the model has no such table. Take it off the list.",
                "listed_and_owned: is listed as a table of the service, but its rows carry an owner. Take it off the list.",
                "nobodys: says neither whose its rows are nor that they are the service's. Implement IUserOwned on Nobodys, or list the table in OwnershipRules.ServiceTables with what it holds.",
                "not_stored: NotStored.UserId is not stored. Map it as a column.",
                "points_elsewhere: the owner has no foreign key that ends at accounts. Point it at the account, or together with the key of the row this one hangs on at that row's owner.",
                "through_a_row_of_nobody: the owner has no foreign key that ends at accounts. Point it at the account, or together with the key of the row this one hangs on at that row's owner.",
                "unanchored: the owner has no foreign key that ends at accounts. Point it at the account, or together with the key of the row this one hangs on at that row's owner.",
            ],
            OwnershipRules.FindViolations(context.Model, typeof(Account), serviceTables));
    }

    /// <summary>A model with deliberate violations, and with the tables the rule asks for.</summary>
    private sealed class PlantedContext() : DbContext(new DbContextOptionsBuilder().UseNpgsql().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>().ToTable("accounts");
            modelBuilder.Entity<Reference>().ToTable("reference");

            // Fine: the owner points at the account.
            modelBuilder.Entity<Owned>(owned =>
            {
                owned.ToTable("owned");
                owned.HasOne<Account>().WithMany().HasForeignKey(row => row.UserId);
            });

            // Fine: the owner is carried along to a row that points at the account, as a
            // component does that may not point at the account itself.
            modelBuilder.Entity<OwnedThroughARow>(owned =>
            {
                owned.ToTable("owned_through_a_row");
                owned.HasOne<Owned>().WithMany()
                    .HasForeignKey(row => new { row.OwnedId, row.UserId })
                    .HasPrincipalKey(row => new { row.Id, row.UserId });
            });

            modelBuilder.Entity<Nobodys>().ToTable("nobodys");
            modelBuilder.Entity<Unanchored>().ToTable("unanchored");

            modelBuilder.Entity<PointsElsewhere>(owned =>
            {
                owned.ToTable("points_elsewhere");
                owned.HasOne<Reference>().WithMany().HasForeignKey(row => row.UserId);
            });

            // The row it hangs on has an owner that nothing holds to being an account.
            modelBuilder.Entity<ThroughARowOfNobody>(owned =>
            {
                owned.ToTable("through_a_row_of_nobody");
                owned.HasOne<Unanchored>().WithMany()
                    .HasForeignKey(row => new { row.UnanchoredId, row.UserId })
                    .HasPrincipalKey(row => new { row.Id, row.UserId });
            });

            modelBuilder.Entity<NotStored>(owned =>
            {
                owned.ToTable("not_stored");
                owned.Ignore(row => row.UserId);
            });

            modelBuilder.Entity<ListedAndOwned>(owned =>
            {
                owned.ToTable("listed_and_owned");
                owned.HasOne<Account>().WithMany().HasForeignKey(row => row.UserId);
            });
        }
    }

    private sealed class Account
    {
        public Guid Id { get; set; }
    }

    private sealed class Reference
    {
        public Guid Id { get; set; }
    }

    private sealed class Owned : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class OwnedThroughARow : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid OwnedId { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class Nobodys
    {
        public Guid Id { get; set; }
    }

    private sealed class Unanchored : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class PointsElsewhere : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class ThroughARowOfNobody : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UnanchoredId { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class NotStored : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }

    private sealed class ListedAndOwned : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }
    }
}
