namespace Ecnanib.Platform.Contracts;

/// <summary>The stored names are the member names, so renaming a member needs a migration.</summary>
public enum JobStatus
{
    /// <summary>The work never ran. This is only ever returned, because there's no run to store it on.</summary>
    NotStarted,

    Running,

    Succeeded,

    /// <summary>The work threw.</summary>
    Failed,

    /// <summary>The run passed its timeout.</summary>
    TimedOut,

    /// <summary>Shutdown or a crash ended the run before it finished.</summary>
    Interrupted,
}
