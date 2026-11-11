using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Konscious.Security.Cryptography;

namespace Ecnanib.Tests.Accounts;

/// <summary>Passwords are stored only as salted hashes from a memory-hard algorithm (NFR-SEC-2).</summary>
public partial class PasswordStorageTests
{
    private const string Password = "PASSWORD-MARKER a long phrase 7d1f";

    private static readonly PasswordHasher Hasher = new();

    [Fact]
    public async Task The_password_is_stored_only_as_a_salted_Argon2id_hash()
    {
        using var application = new AccountsApplication();

        await application.RegisterAsync("ada@example.com", Password);

        // Argon2id with 19 MiB of memory and two passes, a salt of 16 bytes, and a hash of 32.
        var user = StoredUser.Of(application.Database, "ada@example.com")!;
        Assert.Matches(StoredHash(), user.PasswordHash);

        // The hash is of that password, and of no other.
        Assert.True(await Hasher.VerifyAsync(Password, user.PasswordHash));
        Assert.False(await Hasher.VerifyAsync(Password + "!", user.PasswordHash));

        // Nothing else in the database, in any column of any table, holds the password.
        var everything = string.Join(
            Environment.NewLine,
            application.Database.Tables().SelectMany(table =>
                application.Database.Column($"""SELECT row_to_json(stored)::text FROM "{table}" stored""")));
        Assert.Contains(user.PasswordHash, everything);
        Assert.DoesNotContain("PASSWORD-MARKER", everything);
    }

    [Fact]
    public async Task The_same_password_gives_every_account_another_hash()
    {
        using var application = new AccountsApplication();

        await application.RegisterAsync("ada@example.com", Password);
        await application.RegisterAsync("grace@example.com", Password);

        var ada = StoredUser.Of(application.Database, "ada@example.com")!.PasswordHash;
        var grace = StoredUser.Of(application.Database, "grace@example.com")!.PasswordHash;

        // Each has a salt of its own, so neither the salt nor the hash is shared.
        Assert.NotEqual(ada.Split('$')[4], grace.Split('$')[4]);
        Assert.NotEqual(ada.Split('$')[5], grace.Split('$')[5]);
        Assert.True(await Hasher.VerifyAsync(Password, ada));
        Assert.True(await Hasher.VerifyAsync(Password, grace));
    }

    [Fact]
    public async Task A_hash_made_with_other_parameters_still_verifies()
    {
        // Made here with the library itself, with less memory, one pass, two lanes, and a
        // shorter hash than the application uses: as a hash from an earlier version would be.
        var salt = Encoding.ASCII.GetBytes("an-earlier-salt!");
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(Password))
        {
            Salt = salt,
            MemorySize = 8_192,
            Iterations = 1,
            DegreeOfParallelism = 2,
        };
        var earlier = $"$argon2id$v=19$m=8192,t=1,p=2${Unpadded(salt)}${Unpadded(await argon2.GetBytesAsync(24))}";

        Assert.True(await Hasher.VerifyAsync(Password, earlier));
        Assert.False(await Hasher.VerifyAsync("another password", earlier));

        // The parameters are part of what is verified: the same salt and hash under others do not match.
        Assert.False(await Hasher.VerifyAsync(Password, earlier.Replace("t=1", "t=2")));
    }

    [Fact]
    public async Task A_password_typed_in_another_Unicode_form_is_the_same_password()
    {
        // The same letter as one character, and as a letter followed by its accent.
        var composed = "café au lait, s'il vous plaît";
        var decomposed = "café au lait, s'il vous plaît";
        Assert.NotEqual(composed, decomposed);

        var hash = await Hasher.HashAsync(composed);

        Assert.True(await Hasher.VerifyAsync(decomposed, hash));
        Assert.False(await Hasher.VerifyAsync("cafe au lait, s'il vous plait", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PASSWORD-MARKER a long phrase 7d1f")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$not base64")]
    [InlineData("$argon2id$v=19$m=0,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g")]
    [InlineData("$argon2id$v=16$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g")]
    [InlineData("$2b$12$abcdefghijklmnopqrstuuJ8Zz0y3xM1aQ9w7v5t3r1p9n7l5j3h1")]
    public async Task What_is_not_a_hash_of_this_kind_matches_no_password(string stored)
    {
        Assert.False(await Hasher.VerifyAsync(Password, stored));
    }

    [Fact]
    public async Task Neither_the_password_nor_the_link_reaches_the_log()
    {
        // With the host's own events about each request turned on, which name its address.
        using var application = new AccountsApplication(
            settings: file => file.WithJson("Logging:Overrides", """{ "Microsoft.AspNetCore": "Information" }"""));
        using var client = application.CreateClient();

        var (path, _) = await client.SubmitAsync(
            "/register",
            "register",
            ("Email", "ada@example.com"),
            ("Password", Password),
            ("TermsOfService", LegalDocuments.CurrentVersionOf(LegalDocument.TermsOfService)),
            ("PrivacyPolicy", LegalDocuments.CurrentVersionOf(LegalDocument.PrivacyPolicy)),
            ("RiskDisclosure", LegalDocuments.CurrentVersionOf(LegalDocument.RiskDisclosure)));
        Assert.Equal("/register/sent", path);

        var link = AccountsApplication.LinkIn(Assert.Single(application.Email.Sent));
        var token = AccountsApplication.TokenIn(application.Email.Sent[0]);
        var (_, confirmed) = await client.SubmitAsync(link.PathAndQuery, "verify-email");
        Assert.Contains("Your email address is confirmed.", confirmed);

        var log = application.LogText();

        // The requests are in the log, the one with the link included, with its token masked.
        Assert.Contains("/register", log);
        Assert.Contains("/verify-email", log);
        Assert.Contains("token=***", log);
        Assert.DoesNotContain(token, log);
        Assert.DoesNotContain("PASSWORD-MARKER", log);
        Assert.DoesNotContain(StoredUser.Of(application.Database, "ada@example.com")!.PasswordHash, log);
    }

    private static string Unpadded(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=19456,t=2,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$")]
    private static partial Regex StoredHash();
}
