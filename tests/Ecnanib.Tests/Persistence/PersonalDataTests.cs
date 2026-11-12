using System.Net;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Personal data is limited to the email address, the password hash, and preferences
/// (NFR-SEC-9). These tests hold the columns of the model to <see cref="PersonalDataRules"/>.
/// They read the model and need no database.
/// </summary>
public class PersonalDataTests
{
    [Fact]
    public void Every_column_that_could_say_who_a_user_is_is_listed_with_what_it_holds()
    {
        using var context = ModelOnly.Context();

        var violations = PersonalDataRules.FindViolations(context.Model);

        Assert.True(
            violations.Count == 0,
            "Personal data is limited to the email address, the password hash, and preferences. See docs/accounts.md:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void What_is_stored_about_a_person_is_the_address_the_password_hash_and_one_thing_beyond_the_list()
    {
        // Pinned, so that a change to either list shows in a review as what it is: a decision
        // about what the application keeps about a person.
        Assert.Equal(
            new Dictionary<string, PersonalDataKind>
            {
                ["users.email"] = PersonalDataKind.EmailAddress,
                ["users.password_hash"] = PersonalDataKind.PasswordHash,
            },
            PersonalDataRules.Personal);

        Assert.Equal(["audit_events.origin"], PersonalDataRules.BeyondTheList.Keys);
    }

    [Fact]
    public void No_column_is_listed_as_two_things_and_each_says_what_it_holds()
    {
        var twice = PersonalDataRules.Listed.GroupBy(name => name).Where(names => names.Count() > 1).Select(names => names.Key);

        Assert.Empty(twice);
        Assert.All(PersonalDataRules.BeyondTheList.Concat(PersonalDataRules.NotPersonal), column =>
            Assert.False(string.IsNullOrWhiteSpace(column.Value), $"{column.Key} does not say what it holds."));
    }

    [Fact]
    public void A_column_that_could_say_who_a_user_is_and_is_not_listed_is_found()
    {
        using var context = new PlantedContext();

        string[] listed =
        [
            "accounts.email",        // Fine: text, and listed.
            "accounts.id",           // Not fine: an id says nothing.
            "accounts.phone",        // Not fine: no such column.
            "notes.picture",         // Fine: bytes, and listed.
        ];

        Assert.Equal(
            [
                "accounts.born_on: could say who a user is, and is not listed. List it in PersonalDataRules: as personal data of a kind that NFR-SEC-9 allows, or with what it holds instead.",
                "accounts.id: is listed, but its type cannot say who someone is. Take it off the list.",
                "accounts.nickname: could say who a user is, and is not listed. List it in PersonalDataRules: as personal data of a kind that NFR-SEC-9 allows, or with what it holds instead.",
                "accounts.phone: is listed, but no table of users' rows has such a column. Take it off the list.",
                "notes.seen_from: could say who a user is, and is not listed. List it in PersonalDataRules: as personal data of a kind that NFR-SEC-9 allows, or with what it holds instead.",
                "notes.text: could say who a user is, and is not listed. List it in PersonalDataRules: as personal data of a kind that NFR-SEC-9 allows, or with what it holds instead.",
            ],
            PersonalDataRules.FindViolations(context.Model, typeof(Account), listed));
    }

    /// <summary>
    /// A model with deliberate violations, and with columns the rule leaves alone. Its columns
    /// are named as the application names them.
    /// </summary>
    private sealed class PlantedContext()
        : DbContext(new DbContextOptionsBuilder().UseNpgsql().UseSnakeCaseNamingConvention().Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>().ToTable("accounts");
            modelBuilder.Entity<Note>(note =>
            {
                note.ToTable("notes");
                note.HasOne<Account>().WithMany().HasForeignKey(row => row.UserId);
            });

            // A table of the service holds nothing about a user, whatever its columns are.
            modelBuilder.Entity<Reference>().ToTable("reference");
        }
    }

    private enum Mood
    {
        Calm,
    }

    private sealed class Account
    {
        public Guid Id { get; set; }

        public string Email { get; set; } = string.Empty;

        public string? Nickname { get; set; }

        public DateOnly? BornOn { get; set; }

        // None of these can say who someone is.
        public bool IsVerified { get; set; }

        public int FailedCodes { get; set; }

        public long? LastStep { get; set; }

        public decimal Limit { get; set; }

        public DateTimeOffset? VerifiedAt { get; set; }

        public Mood Mood { get; set; }
    }

    private sealed class Note : IUserOwned
    {
        public Guid Id { get; set; }

        public Guid UserId { get; set; }

        public string Text { get; set; } = string.Empty;

        public byte[] Picture { get; set; } = [];

        public IPAddress? SeenFrom { get; set; }

        public DateTimeOffset WrittenAt { get; set; }
    }

    private sealed class Reference
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
