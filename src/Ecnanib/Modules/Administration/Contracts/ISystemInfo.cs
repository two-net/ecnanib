namespace Ecnanib.Modules.Administration.Contracts;

/// <summary>Application version and uptime for the system health page (FR-ADM-04).</summary>
public interface ISystemInfo
{
    string Version { get; }

    TimeSpan Uptime { get; }
}
