using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecnanib.Kernel;

/// <summary>
/// A decrypted secret, such as a user's API key or TOTP secret (FR-KEY-09). It never shows its value:
/// <see cref="ToString"/> and the debugger show a mask, logging masks it with or without <c>@</c>, and
/// serializing it to JSON throws, so it can't reach an export or a decision trace. Reveal it only
/// where it's used, such as when signing a request, and dispose it straight after.
/// See Secrets in docs/architecture.md.
/// </summary>
/// <remarks>
/// The one Kernel type with behaviour (rule 5). The adapters need to carry a secret to where it's
/// used, and they can't reference Platform.
/// </remarks>
[DebuggerDisplay(Mask)]
[JsonConverter(typeof(RefuseJson))]
public sealed class SecretValue : IDisposable
{
    /// <summary>What it shows instead of its value: the logging pipeline's mask.</summary>
    public const string Mask = "***";

    /// <summary>Strict, so a string that isn't valid text fails here rather than changing silently.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _utf8;
    private bool _disposed;

    /// <param name="utf8">Pinned, so the collector never leaves a copy that Dispose can't clear. This takes it over.</param>
    private SecretValue(byte[] utf8) => _utf8 = utf8;

    public static SecretValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var utf8 = GC.AllocateUninitializedArray<byte>(Utf8.GetByteCount(value), pinned: true);
        Utf8.GetBytes(value, utf8);

        return new SecretValue(utf8);
    }

    /// <summary>Copies <paramref name="utf8"/>, so the caller still clears its own buffer.</summary>
    public static SecretValue FromUtf8(ReadOnlySpan<byte> utf8)
    {
        var copy = GC.AllocateUninitializedArray<byte>(utf8.Length, pinned: true);
        utf8.CopyTo(copy);

        return new SecretValue(copy);
    }

    /// <summary>
    /// The value as a string, which stays in memory until the collector reclaims it. Prefer
    /// <see cref="RevealUtf8"/> where the consumer takes bytes, as an HMAC does.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The value was disposed.</exception>
    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Utf8.GetString(_utf8);
    }

    /// <summary>The value's UTF-8 bytes, valid until this is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The value was disposed.</exception>
    public ReadOnlySpan<byte> RevealUtf8()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _utf8;
    }

    public override string ToString() => Mask;

    /// <summary>Clears the value. Revealing it afterwards throws.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_utf8);
        _disposed = true;
    }

    /// <summary>A secret has no JSON form, so it can't end up in an export, a trace or a notification.</summary>
    private sealed class RefuseJson : JsonConverter<SecretValue>
    {
        public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A secret is never read from JSON (FR-KEY-09).");

        public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options) =>
            throw new NotSupportedException("A secret is never written as JSON (FR-KEY-09).");
    }
}
