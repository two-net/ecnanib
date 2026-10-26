using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Configuration;

namespace Ecnanib.Tests.Support;

/// <summary>
/// The configuration file of a process-level start: the shipped reference file (<c>appsettings.json</c>, copied beside
/// the tests) with the listen endpoint, <c>Server</c> and <c>Database</c> pointed where the test says, written as
/// <c>appsettings.json</c> into the directory the process starts in (its content root), so every other section starts
/// the real process with its documented defaults.
/// </summary>
internal static class StartupConfig
{
    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    public static string Write(TempDirectory directory, int port, string connectionString, int retryOnFailureCount = 0, int instanceLockWaitSeconds = 0, string? listenUrl = null)
    {
        var file = JsonNode.Parse(File.ReadAllText(ReferenceConfiguration.Path), documentOptions: Reading)!.AsObject();
        file["Kestrel"]!["Endpoints"]!["Http"]!["Url"] = listenUrl ?? $"http://127.0.0.1:{port}";
        file["Server"]!["PublicUrl"] = $"http://127.0.0.1:{port}";
        var database = file["Database"]!.AsObject();
        database["ConnectionString"] = connectionString;
        database["RetryOnFailureCount"] = retryOnFailureCount;
        database["InstanceLockWaitSeconds"] = instanceLockWaitSeconds;
        return directory.Write(ConfigurationFile.FileName, file.ToJsonString(Writing));
    }
}
