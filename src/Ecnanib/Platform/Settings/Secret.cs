namespace Ecnanib.Platform.Settings;

/// <summary>
/// A secret from the configuration file, such as a password or a master key. It prints as
/// <c>***</c>, so a secret that reaches a log line, an error message, or a serializer by accident
/// is not disclosed. Call <see cref="Reveal"/> at the point of use.
/// </summary>
public sealed class Secret
{
    private readonly string _value;

    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "***";
}
