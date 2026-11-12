using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// A recovery code of an account that has not been used, as a row of the
/// <c>two_factor_recovery_codes</c> table. A recovery code signs in once in place of a code from
/// the authenticator app, for a user who has lost the app. See docs/accounts.md.
/// </summary>
/// <param name="userId">The account the code belongs to.</param>
/// <param name="codeHash">The SHA-256 hash of the code. The code itself is shown once and not kept.</param>
/// <param name="createdAt">When the code was made.</param>
public sealed class RecoveryCode(Guid userId, byte[] codeHash, DateTimeOffset createdAt) : IUserOwned
{
    public Guid UserId { get; } = userId;

    public byte[] CodeHash { get; } = codeHash;

    public DateTimeOffset CreatedAt { get; } = createdAt;
}

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("two_factor_recovery_codes");

        // A code is looked up for one account, by its hash. A code that is used is deleted.
        builder.HasKey(code => new { code.UserId, code.CodeHash });
        builder.Property(code => code.UserId);
        builder.Property(code => code.CodeHash);
        builder.Property(code => code.CreatedAt);

        // The codes belong to the account and go with it when it is deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(code => code.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>How recovery codes are made, and how one that a user typed is recognised.</summary>
internal static class RecoveryCodes
{
    // 80 random bits: nobody finds one by trying, so a plain hash is enough to store it.
    private const int Length = 10;

    // As many letters and digits as 80 bits make in Base32, shown in groups of four.
    private const int Characters = 16;

    /// <summary>A new code, as the text to show once and as the hash for the database.</summary>
    public static (string Text, byte[] Hash) Create()
    {
        var code = Totp.TextOf(RandomNumberGenerator.GetBytes(Length));

        return ($"{code[..4]}-{code[4..8]}-{code[8..12]}-{code[12..]}", SHA256.HashData(Encoding.ASCII.GetBytes(code)));
    }

    /// <summary>
    /// The hash of a code that a user typed, or null when the text cannot be a recovery code. The
    /// code is the same in small letters and capitals, and with or without its hyphens and spaces.
    /// </summary>
    public static byte[]? HashOf(string text)
    {
        Span<byte> code = stackalloc byte[Characters];
        var length = 0;

        foreach (var character in text)
        {
            if (character == '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            var letter = char.ToUpperInvariant(character);
            if (length == Characters || letter is not ((>= 'A' and <= 'Z') or (>= '2' and <= '7')))
            {
                return null;
            }

            code[length++] = (byte)letter;
        }

        return length == Characters ? SHA256.HashData(code) : null;
    }
}
