namespace Ecnanib.Tests.Ai.Fixtures;

/// <summary>
/// Embedded provider payloads. <c>{{KEY}}</c> and <c>{{MASKED_KEY}}</c> placeholders are substituted at load time so
/// no fixture holds a literal key.
/// </summary>
public static class Fixture
{
    public static string Read(string path, string? key = null, string? maskedKey = null)
    {
        var suffix = "." + path.Replace('/', '.').Replace('\\', '.');
        var assembly = typeof(Fixture).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"Embedded fixture '{path}' not found.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (key is not null)
            text = text.Replace("{{KEY}}", key, StringComparison.Ordinal);
        if (maskedKey is not null)
            text = text.Replace("{{MASKED_KEY}}", maskedKey, StringComparison.Ordinal);
        return text;
    }
}
