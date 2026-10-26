using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;

namespace Ecnanib.Configuration;

/// <summary>
/// The configuration files, the hosting framework's standard ones (FR-CFG-01, C-11): <c>appsettings.json</c> in the
/// working directory (the content root) and, over it, <c>appsettings.{Environment}.json</c>; environment variables and
/// the command line override both (FR-CFG-02). This class checks what the files contain: every top-level section must
/// be one a module or the framework reads, so a misspelt or stale section refuses the start instead of being ignored
/// (FR-CFG-06). The options are bound once at start, so a change takes effect on restart (FR-CFG-07).
/// </summary>
public static class ConfigurationFile
{
    public const string FileName = "appsettings.json";

    /// <summary>The <c>Logging</c> section: the logging module's <c>LoggingOptions</c> (per-sink minimum levels and the shared category levels); pinned to its <c>SectionName</c> by a test.</summary>
    public const string LoggingSection = "Logging";

    /// <summary>
    /// Every top-level section a module reads (FR-CFG-08). Each module's issue adds its own name here; the literals are
    /// pinned to the modules' <c>SectionName</c> constants by a test, so this module depends on no other.
    /// </summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        ServerOptions.SectionName, "Database", LoggingSection, "Exchange", "Providers", "Agents", "RiskCaps", "Paper", "Retention", "Accounts", "Keys", "Council", "Experience", "MarketData", "Execution", "Ui",
    ];

    /// <summary>The framework's own top-level sections: Kestrel's endpoints and the host filtering middleware's allowed hosts.</summary>
    public static readonly IReadOnlyList<string> FrameworkSections = ["Kestrel", "AllowedHosts"];

    /// <summary>The environment's overlay, <c>appsettings.Development.json</c> for Development.</summary>
    public static string EnvironmentFileName(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        return $"appsettings.{environment}.json";
    }

    /// <summary>
    /// Checks every JSON file the configuration was built from: a top-level section that is neither a module's nor the
    /// framework's (nor <c>$schema</c>) is reported as a <see cref="ConfigurationException"/> that names the file and
    /// lists the known sections (FR-CFG-06). Environment variables and the command line are not checked: they carry the
    /// host's own settings too.
    /// </summary>
    public static void VerifySections(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var provider in configuration.Providers.OfType<JsonConfigurationProvider>())
        {
            var unknown = provider.GetChildKeys([], null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !IsKnown(key))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknown.Count == 0)
                continue;

            throw new ConfigurationException(
                $"Configuration file {Describe(provider)} has unknown top-level section(s): {string.Join(", ", unknown.Select(key => $"'{key}'"))}. " +
                $"Known sections: {string.Join(", ", Sections.Concat(FrameworkSections).Order(StringComparer.OrdinalIgnoreCase))}.");
        }
    }

    /// <summary>
    /// The reason a file could not be loaded, for the console (FR-CFG-06): the framework's own message names the file;
    /// a syntax error adds its line and column and the parser's message, a duplicate key its path, an array at the root
    /// is reported as such.
    /// </summary>
    public static string DescribeLoadFailure(InvalidDataException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        JsonException? json = null;
        FormatException? format = null;
        Exception deepest = exception;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            json ??= inner as JsonException;
            format ??= inner as FormatException;
            deepest = inner;
        }

        var file = exception.Message.Trim();
        if (json is not null)
        {
            var message = json.Message;
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut >= 0)
                message = message[..cut];
            // System.Text.Json counts lines and bytes from zero.
            return $"{file} It is not valid JSON at line {json.LineNumber + 1}, column {json.BytePositionInLine + 1}: {message}";
        }

        return $"{file} {(format ?? deepest).Message}";
    }

    private static bool IsKnown(string key) =>
        string.Equals(key, "$schema", StringComparison.OrdinalIgnoreCase)
        || Sections.Contains(key, StringComparer.OrdinalIgnoreCase)
        || FrameworkSections.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's full path when the provider knows it, else the name it was asked for.</summary>
    private static string Describe(JsonConfigurationProvider provider)
    {
        var path = provider.Source.Path ?? FileName;
        return provider.Source.FileProvider?.GetFileInfo(path).PhysicalPath ?? path;
    }
}
