using System.Security.Cryptography;
using Ecnanib.Accounts.Services;
using Ecnanib.Database.Entities;
using Microsoft.AspNetCore.Identity;

namespace Ecnanib.Tests.Accounts.Services;

/// <summary>NFR-SEC-01: Argon2id in PHC form, verified with the stored parameters, upgraded when they are weaker than the current ones.</summary>
public class Argon2idPasswordHasherTests
{
    private const string Password = "correct horse battery staple";
    private static readonly User Anyone = new() { Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM", PasswordHash = "", SecurityStamp = "" };
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void ProducesAPhcStringWithTheCurrentParameters()
    {
        var encoded = _hasher.HashPassword(Anyone, Password);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", encoded);
        Assert.DoesNotContain(Password, encoded);
        Assert.True(Argon2idPasswordHasher.TryParse(encoded, out var memory, out var iterations, out var parallelism, out var salt, out var hash));
        Assert.Equal((65_536, 3, 1), (memory, iterations, parallelism));
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void VerifiesTheRightPasswordAndRejectsTheWrongOne()
    {
        var encoded = _hasher.HashPassword(Anyone, Password);

        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(Anyone, encoded, Password));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(Anyone, encoded, Password + "!"));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(Anyone, encoded, ""));
        Assert.NotEqual(encoded, _hasher.HashPassword(Anyone, Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2id$hash")]
    [InlineData("AQAAAAIAAYagAAAAEBpvbnNlbnNlbm9uc2Vuc2Vub25zZW5zZQ")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=0,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$!!!!$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA$")]
    public void MalformedHashesFailWithoutThrowing(string encoded)
    {
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(Anyone, encoded, Password));
    }

    [Fact]
    public void FlagsAHashMadeWithWeakerParametersForRehash()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var weaker = Argon2idPasswordHasher.Format(8192, 1, 1, salt, Argon2idPasswordHasher.Compute(Password, salt, 8192, 1, 1, 32));

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _hasher.VerifyHashedPassword(Anyone, weaker, Password));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(Anyone, weaker, Password + "!"));
    }

    [Fact]
    public void ATamperedHashFails()
    {
        var encoded = _hasher.HashPassword(Anyone, Password);
        var last = encoded[^1];
        var tampered = encoded[..^1] + (last == 'A' ? 'B' : 'A');

        Assert.Equal(PasswordVerificationResult.Failed, _hasher.VerifyHashedPassword(Anyone, tampered, Password));
    }
}
