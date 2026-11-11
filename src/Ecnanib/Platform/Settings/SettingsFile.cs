using System.Text.Json;

namespace Ecnanib.Platform.Settings;

/// <summary>
/// The one configuration file. Every application setting, secrets included, comes from it and from
/// nowhere else (NFR-CFG-1, NFR-CFG-2). It is read once, at startup.
/// </summary>
public static class SettingsFile
{
    /// <summary>The real file. It holds secrets, so it is kept out of source control.</summary>
    public const string Name = "appsettings.json";

    /// <summary>The copy with placeholders that is kept in source control (NFR-CFG-5).</summary>
    public const string TemplateName = "appsettings.template.json";

    // An appsettings file may carry comments and trailing commas.
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads and validates the file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidSettingsException">
    /// The file is missing, is open to other accounts, is not valid JSON, or breaks a rule. The
    /// exception lists every problem.
    /// </exception>
    public static EcnanibSettings Load(string path)
    {
        var problems = new List<SettingsProblem>();

        if (!File.Exists(path))
        {
            problems.Add(new SettingsProblem(null,
                $"the file does not exist. Copy {TemplateName} to {Name}, replace the placeholders, " +
                "and make the file readable only by the account that runs the application"));

            throw Invalid(path, problems);
        }

        if (IsOpenToOtherAccounts(path))
        {
            problems.Add(new SettingsProblem(null,
                "the file can be accessed by other accounts. It holds secrets, so only the account that " +
                $"runs the application may have access: chmod 600 {Name}"));
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, JsonOptions);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var reader = new SettingsReader(document.RootElement);
                var settings = EcnanibSettings.Read(reader);
                reader.ReportUnknownKeys();

                problems.AddRange(reader.Problems);
                if (problems.Count == 0)
                {
                    return settings;
                }
            }
            else
            {
                problems.Add(new SettingsProblem(null, "the file must hold one JSON object"));
            }
        }
        catch (JsonException malformed)
        {
            problems.Add(NotValidJson(malformed));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            problems.Add(new SettingsProblem(null, "the file cannot be read by the account that runs the application"));
        }

        throw Invalid(path, problems);
    }

    /// <summary>
    /// Turns the host's own failure to read a configuration file into the application's refusal to
    /// start. While it is created, the default host reads the appsettings files in the content
    /// root itself, before the application can remove those sources. It fails when one of them is
    /// not a JSON object with distinct keys, and it does so before <see cref="Load"/> runs.
    /// </summary>
    internal static InvalidSettingsException RejectedByHost(InvalidDataException rejected)
    {
        JsonException? malformed = null;
        for (var cause = rejected.InnerException; cause is not null && malformed is null; cause = cause.InnerException)
        {
            malformed = cause as JsonException;
        }

        var problem = malformed is null
            ? new SettingsProblem(null, "the file must hold one JSON object in which no key appears twice")
            : NotValidJson(malformed);

        // The host's message names the file and nothing else.
        return new InvalidSettingsException(rejected.Message, [problem]);
    }

    private static InvalidSettingsException Invalid(string path, IReadOnlyList<SettingsProblem> problems) =>
        new($"The configuration file {path} is invalid:", problems);

    // The parser's own message can quote the file, so only the position is reported.
    private static SettingsProblem NotValidJson(JsonException malformed) =>
        new(null,
            $"the file is not valid JSON at line {malformed.LineNumber + 1}, " +
            $"position {malformed.BytePositionInLine + 1}");

    /// <summary>
    /// True when the group or other accounts have any access to the file. Windows has no
    /// equivalent of these permission bits, so there the rule is left to whoever deploys.
    /// </summary>
    private static bool IsOpenToOtherAccounts(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        const UnixFileMode otherAccounts =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        return (File.GetUnixFileMode(path) & otherAccounts) != 0;
    }
}
