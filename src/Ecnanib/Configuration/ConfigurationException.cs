namespace Ecnanib.Configuration;

/// <summary>
/// A launch-argument or configuration-file problem the operator must fix before the application can start
/// (FR-CFG-06). The message is written to the console as is, so it names the file and what is wrong with it.
/// </summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
