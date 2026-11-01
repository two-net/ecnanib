using System.Diagnostics;
using System.Reflection;
using Ecnanib.Modules.Administration.Contracts;

namespace Ecnanib.Modules.Administration;

internal sealed class SystemInfo : ISystemInfo
{
    private readonly DateTimeOffset _startedAt;

    public SystemInfo()
    {
        using var process = Process.GetCurrentProcess();
        _startedAt = process.StartTime;
    }

    public string Version { get; } =
        typeof(SystemInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public TimeSpan Uptime => DateTimeOffset.Now - _startedAt;
}
