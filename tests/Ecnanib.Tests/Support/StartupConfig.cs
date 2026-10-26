using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Configuration;

namespace Ecnanib.Tests.Support;

/// <summary>
/// A configuration file for a process-level start: the shipped reference file (<c>ecnanib.json</c>, copied beside the
/// tests) with <c>Server</c> and <c>Database</c> pointed where the test says, so every other section starts the real
/// process with its documented defaults.
/// </summary>
internal static class StartupConfig
{
    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    public static string Write(TempDirectory directory, int port, string connectionString, int retryOnFailureCount = 0, int instanceLockWaitSeconds = 0)
    {
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ConfigurationFile.DefaultFileName)), documentOptions: Reading)!.AsObject();
        var server = file["Server"]!.AsObject();
        server["BindAddress"] = "127.0.0.1";
        server["Port"] = port;
        server["PublicUrl"] = $"http://127.0.0.1:{port}";
        var database = file["Database"]!.AsObject();
        database["ConnectionString"] = connectionString;
        database["RetryOnFailureCount"] = retryOnFailureCount;
        database["InstanceLockWaitSeconds"] = instanceLockWaitSeconds;
        return directory.Write(ConfigurationFile.DefaultFileName, file.ToJsonString(Writing));
    }
}
