namespace Ecnanib.Tests.Support;

/// <summary>A throwaway directory for tests that need real files, removed on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ecnanib-tests-").FullName;

    public string Write(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
