namespace Ecnanib.Shared;

/// <summary>
/// Identifies an AI provider by its code, for example <c>claude</c>. Providers are an open set
/// keyed by code, not an enum, so no caller can branch over them and a new provider needs no
/// change outside the AI Provider Gateway. Codes compare without regard to case.
/// </summary>
public readonly record struct AiProviderCode
{
    private readonly string? _value;

    public AiProviderCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value.Trim().ToLowerInvariant();
    }

    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;
}
