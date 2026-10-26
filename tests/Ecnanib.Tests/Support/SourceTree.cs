namespace Ecnanib.Tests.Support;

/// <summary>
/// The repository and the application's source files, for the tests that read the tree itself: the outbound allowlist
/// (NFR-SEC-04) and the architecture rules (NFR-ARCH-01/02). Build output and the vendored browser libraries are left out.
/// </summary>
internal static class SourceTree
{
    public static readonly string RepoRoot = FindRepoRoot();

    public static readonly string ApplicationRoot = Path.Combine(RepoRoot, "src", "Ecnanib");

    /// <summary>Every application source file with one of the extensions (".cs", ".razor", ".js"), sorted, never empty.</summary>
    public static IReadOnlyList<string> ApplicationSources(params string[] extensions)
    {
        var sources = Directory.EnumerateFiles(ApplicationRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.Ordinal)) && !IsBuildOutputOrVendored(f))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException($"No {string.Join("/", extensions)} files under {ApplicationRoot}.");
        return sources;
    }

    /// <summary>The path below <c>src/Ecnanib</c> with forward slashes, e.g. <c>Accounts/Services/AccountService.cs</c>.</summary>
    public static string RelativeToApplication(string path) =>
        Path.GetRelativePath(ApplicationRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsBuildOutputOrVendored(string path)
    {
        var relative = "/" + RelativeToApplication(path);
        return relative.Contains("/obj/", StringComparison.Ordinal)
               || relative.Contains("/bin/", StringComparison.Ordinal)
               || relative.Contains("/wwwroot/lib/", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ecnanib.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Ecnanib.slnx not found above the test directory.");
    }
}
