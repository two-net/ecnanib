using System.Text.Json;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace Ecnanib.Configuration;

/// <summary>
/// The one system configuration file (FR-CFG-01, C-11): <c>ecnanib.json</c> in the current directory unless
/// <c>--config &lt;path&gt;</c> names another. It is the only configuration source; environment variables, user
/// secrets, other files and command-line settings are never read (FR-CFG-02). The file is read once at start, so a
/// change takes effect on restart (FR-CFG-07).
/// </summary>
public static class ConfigurationFile
{
    public const string DefaultFileName = "ecnanib.json";

    public const string ConfigOption = "--config";

    /// <summary>The <c>Logging</c> section: the logging module's <c>LoggingOptions</c> (per-sink minimum levels and the shared category levels); pinned to its <c>SectionName</c> by a test.</summary>
    public const string LoggingSection = "Logging";

    /// <summary>
    /// Every top-level section the file may contain (FR-CFG-08). Each module's issue adds its own name here; the
    /// literals are pinned to the modules' <c>SectionName</c> constants by a test, so this module depends on no other.
    /// </summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        ServerOptions.SectionName, "Database", LoggingSection, "Exchange", "Providers", "Agents", "RiskCaps", "Paper", "Retention", "Accounts", "Keys", "Council", "Experience", "MarketData", "Execution", UiOptions.SectionName,
    ];

    public static readonly string Usage =
        $"Usage: ecnanib [{ConfigOption} <path>]{Environment.NewLine}" +
        $"  {ConfigOption} <path>   Configuration file to use (default: {DefaultFileName} in the current directory).";

    /// <summary>Resolves the file path from the launch arguments; <c>--config</c> is the only argument accepted.</summary>
    public static string ResolvePath(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? path = null;
        for (var i = 0; i < args.Length; i++)
        {
            string? value;
            if (string.Equals(args[i], ConfigOption, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new ConfigurationException($"{ConfigOption} requires a path.{Environment.NewLine}{Usage}");
                value = args[++i];
            }
            else if (args[i].StartsWith(ConfigOption + "=", StringComparison.Ordinal))
            {
                value = args[i][(ConfigOption.Length + 1)..];
            }
            else
            {
                throw new ConfigurationException($"Unknown argument '{args[i]}'.{Environment.NewLine}{Usage}");
            }

            if (string.IsNullOrWhiteSpace(value))
                throw new ConfigurationException($"{ConfigOption} requires a path.{Environment.NewLine}{Usage}");
            if (path is not null)
                throw new ConfigurationException($"{ConfigOption} was given more than once.{Environment.NewLine}{Usage}");
            path = value;
        }

        return Path.GetFullPath(path ?? DefaultFileName);
    }

    /// <summary>
    /// Adds the file as the configuration's only source. A missing, unreadable or malformed file and an unknown
    /// top-level section are reported as a <see cref="ConfigurationException"/> that names the path (FR-CFG-06).
    /// </summary>
    public static void Load(ConfigurationManager configuration, string path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ConfigurationException(NotFound(path));

        try
        {
            // ExclusionFilters.None: a hidden file or a dot-file is still a file, not "not found".
            var provider = new PhysicalFileProvider(Path.GetDirectoryName(path)!, ExclusionFilters.None);
            configuration.AddJsonFile(provider, Path.GetFileName(path), optional: false, reloadOnChange: false);
        }
        catch (FileNotFoundException)
        {
            throw new ConfigurationException(NotFound(path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new ConfigurationException($"Configuration file {path} could not be read: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new ConfigurationException(DescribeParseFailure(path, ex), ex);
        }

        // The JSON provider alone: the builder's own keys (content root, application name) are not sections.
        var unknown = ((IConfigurationRoot)configuration).Providers.OfType<JsonConfigurationProvider>().Single()
            .GetChildKeys([], null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => !string.Equals(key, "$schema", StringComparison.OrdinalIgnoreCase)
                          && !Sections.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigurationException(
                $"Configuration file {path} has unknown top-level section(s): {string.Join(", ", unknown.Select(key => $"'{key}'"))}. " +
                $"Known sections: {string.Join(", ", Sections.Order(StringComparer.OrdinalIgnoreCase))}.");
        }
    }

    private static string NotFound(string path) =>
        $"Configuration file not found: {path}. Create it or pass {ConfigOption} <path>.";

    private static string DescribeParseFailure(string path, Exception ex)
    {
        JsonException? json = null;
        FormatException? format = null;
        var deepest = ex;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            json ??= inner as JsonException;
            format ??= inner as FormatException;
            deepest = inner;
        }

        if (json is not null)
        {
            var message = json.Message;
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut >= 0)
                message = message[..cut];
            // System.Text.Json counts lines and bytes from zero.
            return $"Configuration file {path} is not valid JSON at line {json.LineNumber + 1}, column {json.BytePositionInLine + 1}: {message}";
        }

        return $"Configuration file {path} is not valid: {(format ?? deepest).Message}";
    }
}
