using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Codes for a user who has lost their authenticator. Each is 10 base32 characters, so 50 random bits,
/// which can't be guessed, and only its SHA-256 is stored. Each takes the caller's context, so that
/// using or replacing codes commits with whatever they allow.
/// </summary>
internal static class RecoveryCodes
{
    public const int Count = 10;

    public const int HashLength = 32;

    private const int Length = 10;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private static readonly SearchValues<char> Characters = SearchValues.Create(Alphabet);

    /// <summary>New codes, written as xxxxx-xxxxx so they're easier to copy.</summary>
    public static List<string> New() =>
    [
        .. Enumerable.Range(0, Count).Select(_ =>
        {
            var code = RandomNumberGenerator.GetString(Alphabet, Length);

            return $"{code[..5]}-{code[5..]}";
        }),
    ];

    /// <summary>Whether a normalized code has the shape of a recovery code.</summary>
    public static bool IsCode(string normalized) => normalized.Length == Length && !normalized.AsSpan().ContainsAnyExcept(Characters);

    /// <summary>Of a code that <see cref="TwoFactorCode.Parse"/> normalized, as it was when issued.</summary>
    public static byte[] HashOf(string normalized) => SHA256.HashData(Encoding.ASCII.GetBytes(normalized));

    /// <summary>Replaces the user's codes. Adds to the context without saving.</summary>
    public static async Task ReplaceAsync(EcnanibDbContext context, Guid userId, IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(context, userId, cancellationToken);

        context.Set<RecoveryCode>().AddRange(
            codes.Select(code => new RecoveryCode { UserId = userId, CodeHash = HashOf(TwoFactorCode.Parse(code).Value) }));
    }

    /// <summary>Deletes the code if the user has it, which is what makes it work once. Call it in the caller's transaction.</summary>
    public static async Task<bool> UseAsync(EcnanibDbContext context, Guid userId, byte[] hash, CancellationToken cancellationToken) =>
        await context.Set<RecoveryCode>()
            .Where(code => code.UserId == userId && code.CodeHash == hash)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public static Task<int> DeleteAllAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<RecoveryCode>().Where(code => code.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}

internal enum TwoFactorCodeKind
{
    /// <summary>Neither shape, so it's refused without being checked or charged.</summary>
    None,
    Totp,
    Recovery,
}

/// <summary>
/// What someone typed into a code field, normalized: spaces and hyphens gone and lowercase, since apps
/// show "123 456" and recovery codes are written with a hyphen. One field takes either kind.
/// </summary>
internal readonly record struct TwoFactorCode(TwoFactorCodeKind Kind, string Value)
{
    /// <summary>Longer than anything real, and it bounds the work done on what a visitor sends.</summary>
    private const int MaxLength = 64;

    public static TwoFactorCode Parse(string? code)
    {
        if (code is null || code.Length > MaxLength)
        {
            return new TwoFactorCode(TwoFactorCodeKind.None, string.Empty);
        }

        var normalized = string.Concat(code.Where(character => !char.IsWhiteSpace(character) && character != '-')).ToLowerInvariant();

        var kind = Totp.IsCode(normalized) ? TwoFactorCodeKind.Totp
            : RecoveryCodes.IsCode(normalized) ? TwoFactorCodeKind.Recovery
            : TwoFactorCodeKind.None;

        return new TwoFactorCode(kind, normalized);
    }

    /// <summary>A record's ToString would print the code.</summary>
    public override string ToString() => Kind.ToString();
}
